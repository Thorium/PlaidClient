module WebhookTests

open System
open System.Security.Cryptography
open System.Text
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
