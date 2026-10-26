/// Webhook verification (the Plaid-Verification JWT) and parsing.
module Plaid.Webhook

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type WebhookEvent =
    /// New, changed or removed transactions are ready: run Sync.syncItemAsync.
    | SyncUpdatesAvailable of itemId: string * initialUpdateComplete: bool * historicalUpdateComplete: bool
    /// The item stopped updating, e.g. ITEM_LOGIN_REQUIRED: send the user through update mode.
    | ItemError of itemId: string * error: PlaidApiError voption
    /// UK/EU (and a few US) institutions: consent ends soon, send the user through update mode before it does.
    | PendingExpiration of itemId: string * consentExpiration: DateTimeOffset voption
    /// US/CA institutions: the item will stop working (token expiry, institution migration); send the user through update mode.
    | PendingDisconnect of itemId: string * reason: string voption * disconnectTime: DateTimeOffset voption
    | UserPermissionRevoked of itemId: string
    | UserAccountRevoked of itemId: string * accountId: string voption
    | LoginRepaired of itemId: string
    | NewAccountsAvailable of itemId: string
    | Other of webhookType: string * webhookCode: string * itemId: string voption

/// Plaid signs each webhook for five minutes.
let maxAge = TimeSpan.FromMinutes 5.0

/// The header that carries the signed JWT.
[<Literal>]
let VerificationHeader = "Plaid-Verification"

let private base64UrlDecode (s: string) =
    let standard = s.Replace('-', '+').Replace('_', '/')
    Convert.FromBase64String(standard + String('=', (4 - standard.Length % 4) % 4))

let private decodeJson (part: string) =
    JsonDocument.Parse(ReadOnlyMemory(base64UrlDecode part))

let private sha256Hex (body: string) =
    use sha = SHA256.Create()

    sha.ComputeHash(Encoding.UTF8.GetBytes body)
    |> Array.map _.ToString("x2")
    |> String.Concat

let private fixedTimeEquals (a: string) (b: string) =
    a.Length = b.Length
    && Seq.fold2 (fun acc (x: char) (y: char) -> acc ||| (int x ^^^ int y)) 0 a b = 0

let private signatureValid (key: WebhookVerificationKey) (signingInput: string) (signature: byte[]) =
    let parameters =
        ECParameters(
            Curve = ECCurve.NamedCurves.nistP256,
            Q = ECPoint(X = base64UrlDecode key.X, Y = base64UrlDecode key.Y)
        )

    use ecdsa = ECDsa.Create parameters
    ecdsa.VerifyData(Encoding.ASCII.GetBytes signingInput, signature, HashAlgorithmName.SHA256)

/// Key id the JWT was signed with, to look up with Api.getWebhookVerificationKeyAsync.
let keyIdOf (jwt: string) =
    try
        match jwt.Split '.' with
        | [| header; _; _ |] ->
            use doc = decodeJson header

            match Json.tryString "kid" doc.RootElement with
            | ValueSome kid -> Ok kid
            | ValueNone -> Error "JWT header has no kid"
        | _ -> Error "Not a compact JWS"
    with
    | :? FormatException
    | :? JsonException as e -> Error e.Message

/// Checks the JWT from the Plaid-Verification header against key and the raw request body, exactly as received.
let verifyWithKey (key: WebhookVerificationKey) (body: string) (jwt: string) (now: DateTimeOffset) : Result<unit, string> =
    try
        match jwt.Split '.' with
        | [| headerPart; payloadPart; signaturePart |] ->
            use header = decodeJson headerPart
            use payload = decodeJson payloadPart
            let signature = base64UrlDecode signaturePart

            if Json.tryString "alg" header.RootElement <> ValueSome "ES256" then
                Error "JWT alg is not ES256"
            elif Json.tryString "kid" header.RootElement <> ValueSome key.KeyId then
                Error "JWT kid does not match the key"
            elif key.Algorithm <> "ES256" || key.Curve <> "P-256" then
                Error $"Unsupported key {key.Algorithm}/{key.Curve}"
            elif signature.Length <> 64 || not (signatureValid key $"{headerPart}.{payloadPart}" signature) then
                Error "Signature does not match"
            else
                match Json.tryInt64 "iat" payload.RootElement, Json.tryString "request_body_sha256" payload.RootElement with
                | ValueSome iat, ValueSome bodyHash ->
                    let issued = DateTimeOffset.FromUnixTimeSeconds iat

                    if now - issued > maxAge then
                        Error "Webhook is older than five minutes"
                    // A rotated-out key still verifies what it signed before it expired.
                    elif key.ExpiredAt |> ValueOption.exists (fun expired -> expired < issued) then
                        Error "Signed after the verification key expired"
                    elif not (fixedTimeEquals (sha256Hex body) (bodyHash.ToLowerInvariant())) then
                        Error "Body does not match request_body_sha256"
                    else
                        Ok()
                | _ -> Error "JWT payload lacks iat or request_body_sha256"
        | _ -> Error "Not a compact JWS"
    with
    | :? FormatException
    | :? JsonException
    | :? CryptographicException as e -> Error e.Message

/// Fetches the signing key from Plaid and verifies. Keys rarely change, so a busy endpoint may cache
/// them by key id and call verifyWithKey instead.
let verifyAsync (client: PlaidClient) (body: string) (jwt: string) (now: DateTimeOffset) ct =
    task {
        match keyIdOf jwt with
        | Error reason -> return Error(WebhookRejected reason)
        | Ok kid ->
            match! Api.getWebhookVerificationKeyAsync client kid ct with
            | Error e -> return Error e
            | Ok key -> return verifyWithKey key body jwt now |> Result.mapError WebhookRejected
    }

/// Verification keys by key id: fetched from Plaid on a miss, and again once a cached copy is older than
/// refreshAfter. refreshAfter bounds how long a key Plaid has since rotated out keeps verifying new
/// webhooks, because the cached copy only learns its expired_at when fetched again. Failures are not
/// cached. Safe to share between concurrent requests; keep one per process.
type KeyCache(refreshAfter: TimeSpan) =
    let keys = ConcurrentDictionary<string, struct (WebhookVerificationKey * DateTimeOffset)>()

    member _.RefreshAfter = refreshAfter

    /// The key for keyId: the cached copy while it is younger than RefreshAfter at now, otherwise Plaid's.
    member _.GetAsync
        (client: PlaidClient, keyId: string, now: DateTimeOffset, ct: CancellationToken)
        : Task<Result<WebhookVerificationKey, PlaidFailure>> =
        task {
            match keys.TryGetValue keyId with
            | true, struct (key, fetchedAt) when now - fetchedAt < refreshAfter -> return Ok key
            | _ ->
                match! Api.getWebhookVerificationKeyAsync client keyId ct with
                | Ok key ->
                    keys[keyId] <- struct (key, now)
                    return Ok key
                | Error e -> return Error e
        }

/// Same as verifyAsync, taking the key from cache.
let verifyCachedAsync (cache: KeyCache) (client: PlaidClient) (body: string) (jwt: string) (now: DateTimeOffset) ct =
    task {
        match keyIdOf jwt with
        | Error reason -> return Error(WebhookRejected reason)
        | Ok kid ->
            match! cache.GetAsync(client, kid, now, ct) with
            | Error e -> return Error e
            | Ok key -> return verifyWithKey key body jwt now |> Result.mapError WebhookRejected
    }

let parse (body: string) : Result<WebhookEvent, PlaidFailure> =
    try
        use doc = JsonDocument.Parse body
        let e = doc.RootElement
        let itemId = Json.tryString "item_id" e
        let item () = itemId |> ValueOption.defaultValue ""
        let flag name = Json.tryBool name e |> ValueOption.defaultValue false

        let event =
            match Json.required "webhook_type" Json.tryString e, Json.required "webhook_code" Json.tryString e with
            | "TRANSACTIONS", "SYNC_UPDATES_AVAILABLE" ->
                SyncUpdatesAvailable(item (), flag "initial_update_complete", flag "historical_update_complete")
            | "ITEM", "ERROR" ->
                let error =
                    Json.tryObject "error" e
                    |> ValueOption.filter (fun err -> (Json.tryString "error_code" err).IsSome)
                    |> ValueOption.map (Parse.apiError 0)

                ItemError(item (), error)
            | "ITEM", "PENDING_EXPIRATION" -> PendingExpiration(item (), Json.tryTimestamp "consent_expiration_time" e)
            | "ITEM", "PENDING_DISCONNECT" ->
                PendingDisconnect(item (), Json.tryString "reason" e, Json.tryTimestamp "disconnect_time" e)
            | "ITEM", "USER_PERMISSION_REVOKED" -> UserPermissionRevoked(item ())
            | "ITEM", "USER_ACCOUNT_REVOKED" -> UserAccountRevoked(item (), Json.tryString "account_id" e)
            | "ITEM", "LOGIN_REPAIRED" -> LoginRepaired(item ())
            | "ITEM", "NEW_ACCOUNTS_AVAILABLE" -> NewAccountsAvailable(item ())
            | webhookType, webhookCode -> Other(webhookType, webhookCode, itemId)

        Ok event
    with
    | :? JsonException as ex -> Error(InvalidResponse ex.Message)
    | Json.InvalidJson message -> Error(InvalidResponse message)

/// Verifies, then parses.
let receiveAsync (client: PlaidClient) (body: string) (jwt: string) (now: DateTimeOffset) ct =
    task {
        match! verifyAsync client body jwt now ct with
        | Error e -> return Error e
        | Ok() -> return parse body
    }

/// Verifies with a key from cache, then parses.
let receiveCachedAsync (cache: KeyCache) (client: PlaidClient) (body: string) (jwt: string) (now: DateTimeOffset) ct =
    task {
        match! verifyCachedAsync cache client body jwt now ct with
        | Error e -> return Error e
        | Ok() -> return parse body
    }
