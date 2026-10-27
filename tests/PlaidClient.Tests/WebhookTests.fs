module WebhookTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Plaid
open FakePlaid

let private base64Url (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private sha256Hex (s: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes s) |> Convert.ToHexStringLower

let private now = DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)

let private body =
    """{"webhook_type":"TRANSACTIONS","webhook_code":"SYNC_UPDATES_AVAILABLE","item_id":"item-1","initial_update_complete":true,"historical_update_complete":false,"environment":"sandbox"}"""

/// A P-256 key pair standing in for Plaid's, and the matching key record.
let private signer = ECDsa.Create(ECCurve.NamedCurves.nistP256)

let private key =
    let p = signer.ExportParameters false

    { KeyId = "kid-1"
      Algorithm = "ES256"
      Curve = "P-256"
      X = base64Url p.Q.X
      Y = base64Url p.Q.Y
      ExpiredAt = ValueNone }

let private jwtFor (kid: string) (issuedAt: DateTimeOffset) (signedBody: string) =
    let header = base64Url (Encoding.UTF8.GetBytes $"""{{"alg":"ES256","kid":"{kid}","typ":"JWT"}}""")

    let payload =
        base64Url (
            Encoding.UTF8.GetBytes
                $"""{{"iat":{issuedAt.ToUnixTimeSeconds()},"request_body_sha256":"{sha256Hex signedBody}"}}"""
        )

    let signingInput = $"{header}.{payload}"
    let signature = signer.SignData(Encoding.ASCII.GetBytes signingInput, HashAlgorithmName.SHA256)
    signingInput + "." + base64Url signature

[<Fact>]
let ``valid webhook verifies`` () =
    Assert.Equal(Ok(), Webhook.verifyWithKey key body (jwtFor "kid-1" now body) now)

[<Fact>]
let ``tampered body is rejected`` () =
    let jwt = jwtFor "kid-1" now body

    match Webhook.verifyWithKey key (body.Replace("item-1", "item-2")) jwt now with
    | Error reason -> Assert.Contains("request_body_sha256", reason)
    | Ok() -> failwith "accepted"

[<Fact>]
let ``webhook older than five minutes is rejected`` () =
    let jwt = jwtFor "kid-1" (now.AddMinutes -6.0) body
    Assert.True(Result.isError (Webhook.verifyWithKey key body jwt now))

[<Fact>]
let ``signature from another key is rejected`` () =
    use other = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let p = other.ExportParameters false
    let otherKey = { key with X = base64Url p.Q.X; Y = base64Url p.Q.Y }

    match Webhook.verifyWithKey otherKey body (jwtFor "kid-1" now body) now with
    | Error reason -> Assert.Equal("Signature does not match", reason)
    | Ok() -> failwith "accepted"

[<Fact>]
let ``a key that expired before signing is rejected, one that expired after is not`` () =
    let signedAt = now.AddMinutes -2.0
    let jwt = jwtFor "kid-1" signedAt body
    let expiredBefore = { key with ExpiredAt = ValueSome(signedAt.AddSeconds -1.0) }
    let expiredAfter = { key with ExpiredAt = ValueSome(signedAt.AddSeconds 30.0) }
    Assert.True(Result.isError (Webhook.verifyWithKey expiredBefore body jwt now))
    Assert.Equal(Ok(), Webhook.verifyWithKey expiredAfter body jwt now)

[<Fact>]
let ``mismatched kid is rejected`` () =
    Assert.True(Result.isError (Webhook.verifyWithKey key body (jwtFor "kid-2" now body) now))

[<Fact>]
let ``body hash comparison ignores hex case`` () =
    let header = base64Url (Encoding.UTF8.GetBytes """{"alg":"ES256","kid":"kid-1","typ":"JWT"}""")

    let payload =
        base64Url (
            Encoding.UTF8.GetBytes
                $"""{{"iat":{now.ToUnixTimeSeconds()},"request_body_sha256":"{(sha256Hex body).ToUpperInvariant()}"}}"""
        )

    let signingInput = $"{header}.{payload}"
    let signature = signer.SignData(Encoding.ASCII.GetBytes signingInput, HashAlgorithmName.SHA256)
    Assert.Equal(Ok(), Webhook.verifyWithKey key body (signingInput + "." + base64Url signature) now)

[<Fact>]
let ``garbage token is rejected, not thrown`` () =
    Assert.True(Result.isError (Webhook.verifyWithKey key body "not-a-jwt" now))
    Assert.True(Result.isError (Webhook.verifyWithKey key body "a.b.c" now))

[<Fact>]
let ``receive fetches the key by kid, verifies and parses`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun path request ->
                Assert.Equal("/webhook_verification_key/get", path)
                Assert.Equal("kid-1", request.GetProperty("key_id").GetString())

                ok
                    $"""{{"key":{{"alg":"ES256","created_at":1560466150,"crv":"P-256","expired_at":null,"kid":"kid-1","kty":"EC","use":"sig","x":"{key.X}","y":"{key.Y}"}},"request_id":"r"}}""")

        let! result = Webhook.receiveAsync (clientWith handler) body (jwtFor "kid-1" now body) now CancellationToken.None
        Assert.Equal(Ok(Webhook.SyncUpdatesAvailable("item-1", true, false)), result)
    }

let private keyReply (kid: string) =
    $"""{{"key":{{"alg":"ES256","created_at":1560466150,"crv":"P-256","expired_at":null,"kid":"{kid}","kty":"EC","use":"sig","x":"{key.X}","y":"{key.Y}"}},"request_id":"r"}}"""

let private keyHandler () =
    new FakePlaidHandler(fun _ request -> ok (keyReply (request.GetProperty("key_id").GetString())))

[<Fact>]
let ``cached receive fetches the key once for repeated webhooks`` () : Task =
    task {
        use handler = keyHandler ()
        let client = clientWith handler
        let cache = Webhook.KeyCache(TimeSpan.FromHours 1.0)

        for minute in 0.0 .. 2.0 do
            let at = now.AddMinutes minute
            let! result = Webhook.receiveCachedAsync cache client body (jwtFor "kid-1" at body) at CancellationToken.None
            Assert.Equal(Ok(Webhook.SyncUpdatesAvailable("item-1", true, false)), result)

        Assert.Single handler.Calls |> ignore
    }

[<Fact>]
let ``cached key is fetched again once older than refreshAfter, and per kid`` () : Task =
    task {
        use handler = keyHandler ()
        let client = clientWith handler
        let cache = Webhook.KeyCache(TimeSpan.FromMinutes 10.0)
        let fetch kid (at: DateTimeOffset) = cache.GetAsync(client, kid, at, CancellationToken.None)

        let! _ = fetch "kid-1" now
        let! _ = fetch "kid-1" (now.AddMinutes 9.0)
        Assert.Equal(1, handler.Calls.Length)
        let! _ = fetch "kid-1" (now.AddMinutes 10.0)
        Assert.Equal(2, handler.Calls.Length)
        let! _ = fetch "kid-2" (now.AddMinutes 10.0)

        Assert.Equal<string list>(
            [ "kid-1"; "kid-1"; "kid-2" ],
            handler.Calls |> List.map (fun c -> c.Body.GetProperty("key_id").GetString())
        )
    }

[<Fact>]
let ``a failed key fetch is not cached`` () : Task =
    task {
        let failFirst = ref true

        use handler =
            new FakePlaidHandler(fun _ _ ->
                if failFirst.Value then
                    failFirst.Value <- false
                    plaidError 500 "INTERNAL_SERVER_ERROR"
                else
                    ok (keyReply "kid-1"))

        let client = clientWith handler
        let cache = Webhook.KeyCache(TimeSpan.FromHours 1.0)
        let! first = Webhook.verifyCachedAsync cache client body (jwtFor "kid-1" now body) now CancellationToken.None
        Assert.True(Result.isError first)
        let! second = Webhook.verifyCachedAsync cache client body (jwtFor "kid-1" now body) now CancellationToken.None
        Assert.Equal(Ok(), second)
        Assert.Equal(2, handler.Calls.Length)
    }

[<Fact>]
let ``cached verify still rejects a tampered body`` () : Task =
    task {
        use handler = keyHandler ()
        let cache = Webhook.KeyCache(TimeSpan.FromHours 1.0)
        let jwt = jwtFor "kid-1" now body

        let! result =
            Webhook.verifyCachedAsync cache (clientWith handler) (body.Replace("item-1", "item-2")) jwt now CancellationToken.None

        match result with
        | Error(WebhookRejected reason) -> Assert.Contains("request_body_sha256", reason)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a payment webhook body with Plaid's two-space indentation verifies and parses`` () : Task =
    task {
        // Plaid signs the body exactly as sent, indentation included.
        let paymentBody =
            "{\n  \"webhook_type\": \"PAYMENT_INITIATION\",\n  \"webhook_code\": \"PAYMENT_STATUS_UPDATE\",\n  \"payment_id\": \"payment-id-sandbox-1\",\n  \"new_payment_status\": \"PAYMENT_STATUS_EXECUTED\",\n  \"old_payment_status\": \"PAYMENT_STATUS_INITIATED\",\n  \"timestamp\": \"2026-10-26T11:59:00Z\",\n  \"error\": null,\n  \"environment\": \"sandbox\"\n}"

        use handler = keyHandler ()
        let cache = Webhook.KeyCache(TimeSpan.FromHours 1.0)
        let jwt = jwtFor "kid-1" now paymentBody

        let! result = Webhook.receiveCachedAsync cache (clientWith handler) paymentBody jwt now CancellationToken.None

        match result with
        | Ok(Webhook.PaymentStatusUpdate("payment-id-sandbox-1", Payments.PaymentStatus.Executed, _, ValueSome _, ValueNone)) -> ()
        | other -> failwith $"%A{other}"

        Assert.Equal(200, Webhook.statusCodeFor result)

        // The same JSON re-serialized without the indentation is a different body: rejected.
        let compact = JsonDocument.Parse(paymentBody).RootElement.GetRawText().Replace("\n", "").Replace("  ", "")
        let! rejected = Webhook.receiveCachedAsync cache (clientWith handler) compact jwt now CancellationToken.None
        Assert.Equal(400, Webhook.statusCodeFor rejected)
    }

[<Fact>]
let ``status code tells Plaid to retry only when the key could not be fetched`` () : Task =
    task {
        use failing = new FakePlaidHandler(fun _ _ -> plaidError 500 "INTERNAL_SERVER_ERROR")
        let! keyFetchFailed = Webhook.receiveAsync (clientWith failing) body (jwtFor "kid-1" now body) now CancellationToken.None
        Assert.Equal(503, Webhook.statusCodeFor keyFetchFailed)

        use working = keyHandler ()
        let! forged = Webhook.receiveAsync (clientWith working) body "not-a-jwt" now CancellationToken.None
        Assert.Equal(400, Webhook.statusCodeFor forged)

        let! stale = Webhook.receiveAsync (clientWith working) body (jwtFor "kid-1" (now.AddMinutes -6.0) body) now CancellationToken.None
        Assert.Equal(400, Webhook.statusCodeFor stale)
    }

[<Fact>]
let ``item webhooks parse to their cases`` () =
    let parse (json: string) = Webhook.parse json

    Assert.Equal(
        Ok(Webhook.PendingExpiration("item-1", ValueSome(DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)))),
        parse """{"webhook_type":"ITEM","webhook_code":"PENDING_EXPIRATION","item_id":"item-1","consent_expiration_time":"2027-01-01T00:00:00Z"}"""
    )

    Assert.Equal(
        Ok(
            Webhook.PendingDisconnect(
                "item-1",
                ValueSome "INSTITUTION_TOKEN_EXPIRATION",
                ValueSome(DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero))
            )
        ),
        parse """{"webhook_type":"ITEM","webhook_code":"PENDING_DISCONNECT","item_id":"item-1","reason":"INSTITUTION_TOKEN_EXPIRATION","disconnect_time":"2027-02-01T00:00:00Z"}"""
    )

    match parse """{"webhook_type":"ITEM","webhook_code":"ERROR","item_id":"item-1","error":{"error_type":"ITEM_ERROR","error_code":"ITEM_LOGIN_REQUIRED","error_message":"m","display_message":null,"request_id":"r"}}""" with
    | Ok(Webhook.ItemError("item-1", ValueSome e)) -> Assert.Equal("ITEM_LOGIN_REQUIRED", e.ErrorCode)
    | other -> failwith $"%A{other}"

    Assert.Equal(
        Ok(Webhook.Other("TRANSACTIONS", "DEFAULT_UPDATE", ValueSome "item-1")),
        parse """{"webhook_type":"TRANSACTIONS","webhook_code":"DEFAULT_UPDATE","item_id":"item-1"}"""
    )

    Assert.True(Result.isError (parse "{}"))

[<Fact>]
let ``an item webhook without item_id is an invalid response, not an empty item id`` () =
    match Webhook.parse """{"webhook_type":"TRANSACTIONS","webhook_code":"SYNC_UPDATES_AVAILABLE"}""" with
    | Error(InvalidResponse message) -> Assert.Contains("item_id", message)
    | other -> failwith $"%A{other}"
