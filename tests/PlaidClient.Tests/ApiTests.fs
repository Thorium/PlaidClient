module ApiTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Plaid
open FakePlaid

let private ct = CancellationToken.None

let private strings (e: JsonElement) =
    e.EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq

[<Fact>]
let ``link token request carries credentials as headers and asks for UK transactions`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok """{"link_token":"link-sandbox-1","expiration":"2026-10-26T12:00:00Z","request_id":"r1"}""")

        let! result = Api.createLinkTokenAsync (clientWith handler) "user-42" ct

        match result with
        | Ok token ->
            Assert.Equal("link-sandbox-1", token.LinkToken)
            Assert.Equal(DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), token.Expiration)
        | Error e -> failwith $"%A{e}"

        let call = Assert.Single handler.Calls
        Assert.Equal("/link/token/create", call.Path)
        Assert.Equal("client-1", call.Headers["PLAID-CLIENT-ID"])
        Assert.Equal("secret-1", call.Headers["PLAID-SECRET"])
        Assert.Equal(Api.ApiVersion, call.Headers["Plaid-Version"])
        Assert.False(call.Body.TryGetProperty "secret" |> fst)
        Assert.Equal<string list>([ "GB" ], strings (call.Body.GetProperty "country_codes"))
        Assert.Equal<string list>([ "transactions" ], strings (call.Body.GetProperty "products"))
        Assert.Equal("user-42", call.Body.GetProperty("user").GetProperty("client_user_id").GetString())
        Assert.Equal("https://example.test/oauth", call.Body.GetProperty("redirect_uri").GetString())
        Assert.Equal(730, call.Body.GetProperty("transactions").GetProperty("days_requested").GetInt32())
        Assert.False(call.Body.TryGetProperty "webhook" |> fst, "absent settings are omitted, not sent as null")
    }

[<Fact>]
let ``link token for other products carries their fields and they override the config`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok """{"link_token":"link-sandbox-3","expiration":"2026-10-26T12:00:00Z","request_id":"r1"}""")

        let fields: (string * obj) list =
            [ "user_token", box "user-sandbox-1"
              "income_verification",
              box
                  {| income_source_types = [| "bank" |]
                     bank_income = {| days_requested = 180 |} |}
              "country_codes", box [| "US" |] ]

        let! result =
            Api.createLinkTokenForProductsAsync (clientWith handler) "user-42" [ "assets"; "income_verification" ] fields ct

        Assert.True(Result.isOk result)
        let body = (Assert.Single handler.Calls).Body
        Assert.Equal<string list>([ "assets"; "income_verification" ], strings (body.GetProperty "products"))
        Assert.Equal("user-sandbox-1", body.GetProperty("user_token").GetString())

        Assert.Equal(
            180,
            body.GetProperty("income_verification").GetProperty("bank_income").GetProperty("days_requested").GetInt32()
        )

        Assert.Equal<string list>([ "US" ], strings (body.GetProperty "country_codes"))
        Assert.Equal("Test Co", body.GetProperty("client_name").GetString())
        Assert.False(body.TryGetProperty "transactions" |> fst)
    }

[<Fact>]
let ``postRawAsync reaches any endpoint with credentials in headers and returns the body text`` () : Task =
    task {
        let reply = """{"processor_token":"processor-sandbox-1","request_id":"r9"}"""
        use handler = new FakePlaidHandler(fun _ _ -> ok reply)

        let! result =
            Api.postRawAsync
                (clientWith handler)
                "/processor/token/create"
                {| access_token = "access-1"
                   account_id = "acc-1"
                   processor = "checkbook" |}
                ct

        Assert.Equal(Ok reply, result)
        let call = Assert.Single handler.Calls
        Assert.Equal("/processor/token/create", call.Path)
        Assert.Equal("secret-1", call.Headers["PLAID-SECRET"])
        Assert.Equal("checkbook", call.Body.GetProperty("processor").GetString())
    }

[<Fact>]
let ``postRawAsync returns Plaid errors as ApiError`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> plaidError 400 "PRODUCT_NOT_READY")

        match! Api.postRawAsync (clientWith handler) "/asset_report/get" {| asset_report_token = "t" |} ct with
        | Error(ApiError e) -> Assert.Equal("PRODUCT_NOT_READY", e.ErrorCode)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``update mode link token sends the access token and no products`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok """{"link_token":"link-sandbox-2","expiration":"2026-10-26T12:00:00Z","request_id":"r1"}""")

        let! _ = Api.createUpdateLinkTokenAsync (clientWith handler) "user-42" "access-1" ct
        let call = Assert.Single handler.Calls
        Assert.Equal("access-1", call.Body.GetProperty("access_token").GetString())
        Assert.False(call.Body.TryGetProperty "products" |> fst)
    }

[<Fact>]
let ``update mode link token with fields adds them, e.g. android_package_name`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok """{"link_token":"link-sandbox-4","expiration":"2026-10-26T12:00:00Z","request_id":"r1"}""")

        let fields: (string * obj) list = [ "android_package_name", box "com.example.app" ]

        let! result = Api.createUpdateLinkTokenWithFieldsAsync (clientWith handler) "user-42" "access-1" fields ct
        Assert.True(Result.isOk result)
        let body = (Assert.Single handler.Calls).Body
        Assert.Equal("com.example.app", body.GetProperty("android_package_name").GetString())
        Assert.Equal("access-1", body.GetProperty("access_token").GetString())
        Assert.Equal("user-42", body.GetProperty("user").GetProperty("client_user_id").GetString())
        Assert.False(body.TryGetProperty "products" |> fst)
    }

[<Fact>]
let ``sync sends no options by default`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (syncPageJson [] [] [] "c" false))
        let! _ = Api.syncTransactionsPageAsync (clientWith handler) "access-1" ValueNone 10 ct
        Assert.False((Assert.Single handler.Calls).Body.TryGetProperty "options" |> fst)
    }

[<Fact>]
let ``sync settings become options and original_description is parsed`` () : Task =
    task {
        let withDescription =
            (transactionJson "tx-1" 10m).Replace("\"name\":", "\"original_description\":\"BET365 LTD GIBRALTAR\",\"name\":")

        use handler = new FakePlaidHandler(fun _ _ -> ok (syncPageJson [ withDescription ] [] [] "c" false))

        let client =
            { clientWith handler with
                Config =
                    { config with
                        Sync =
                            { SyncSettings.defaults with
                                IncludeOriginalDescription = true
                                PersonalFinanceCategoryVersion = ValueSome "v2" } } }

        match! Api.syncTransactionsPageAsync client "access-1" ValueNone 10 ct with
        | Ok page ->
            let t = Assert.Single page.Added
            Assert.Equal(ValueSome "BET365 LTD GIBRALTAR", t.OriginalDescription)
        | Error e -> failwith $"%A{e}"

        let options = (Assert.Single handler.Calls).Body.GetProperty "options"
        Assert.True(options.GetProperty("include_original_description").GetBoolean())
        Assert.Equal("v2", options.GetProperty("personal_finance_category_version").GetString())
    }

[<Fact>]
let ``original_description is absent unless Plaid sends it`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (syncPageJson [ transactionJson "tx-1" 10m ] [] [] "c" false))

        match! Api.syncTransactionsPageAsync (clientWith handler) "access-1" ValueNone 10 ct with
        | Ok page -> Assert.Equal(ValueNone, (Assert.Single page.Added).OriginalDescription)
        | Error e -> failwith $"%A{e}"
    }

[<Fact>]
let ``Plaid error body becomes ApiError`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> plaidError 400 "ITEM_LOGIN_REQUIRED")
        match! Api.getAccountsAsync (clientWith handler) "access-1" ct with
        | Error(ApiError e) ->
            Assert.Equal(400, e.HttpStatus)
            Assert.Equal("ITEM_LOGIN_REQUIRED", e.ErrorCode)
            Assert.Equal(ValueSome "Please reconnect", e.DisplayMessage)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``non-JSON failure becomes HttpError with its status`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> 502, "<html>bad gateway</html>")

        match! Api.removeItemAsync (clientWith handler) "access-1" ct with
        | Error(HttpError(status, body) as failure) ->
            Assert.Equal(502, status)
            Assert.Contains("bad gateway", body)
            Assert.Equal(FailureAction.RetryLater, PlaidFailure.action failure)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``success body missing a required field becomes InvalidResponse`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok """{"item_id":"item-1"}""")
        match! Api.exchangePublicTokenAsync (clientWith handler) "public-1" ct with
        | Error(InvalidResponse message) -> Assert.Contains("access_token", message)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``accounts parse balances and item consent expiry`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok
                    $"""{{"accounts":[{accountJson}],"item":{{"item_id":"item-1","institution_id":"ins_1","consent_expiration_time":"2027-04-01T00:00:00Z","error":null}},"request_id":"r"}}""")

        match! Api.getAccountsAsync (clientWith handler) "access-1" ct with
        | Ok r ->
            Assert.Equal(ValueSome "ins_1", r.Item.InstitutionId)
            Assert.Equal(ValueSome(DateTimeOffset(2027, 4, 1, 0, 0, 0, TimeSpan.Zero)), r.Item.ConsentExpirationTime)
            Assert.Equal(ValueNone, r.Item.Error)
            let account = Assert.Single r.Accounts
            Assert.Equal(ValueSome 120.5m, account.Balances.Available)
            Assert.Equal(ValueNone, account.Balances.Limit)
            Assert.Equal(ValueSome "GBP", account.Balances.IsoCurrencyCode)
        | Error e -> failwith $"%A{e}"
    }

[<Fact>]
let ``a number that does not fit decimal becomes InvalidResponse, not an exception`` () : Task =
    task {
        let huge = (transactionJson "tx-1" 1m).Replace("\"amount\":1", "\"amount\":1e400")
        use handler = new FakePlaidHandler(fun _ _ -> ok (syncPageJson [ huge ] [] [] "c" false))
        match! Api.syncTransactionsPageAsync (clientWith handler) "access-1" ValueNone 10 ct with
        | Error(InvalidResponse message) -> Assert.Contains("amount", message)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``printing config and items hides secrets`` () =
    let printedConfig = $"%A{config}" + string config
    Assert.DoesNotContain("secret-1", printedConfig)
    Assert.Contains("client-1", printedConfig)

    let item =
        { ItemId = "item-1"
          UserId = "u"
          AccessToken = "access-secret"
          Cursor = ValueNone
          InstitutionId = ValueNone
          ConsentExpiration = ValueNone }

    let access: ItemAccess = { ItemId = "item-1"; AccessToken = "access-secret" }
    let printed = $"%A{item} %A{access}" + string item + string access
    Assert.DoesNotContain("access-secret", printed)
    Assert.Contains("item-1", printed)

[<Fact>]
let ``US config asks for US institutions`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok """{"link_token":"link-sandbox-1","expiration":"2026-10-26T12:00:00Z","request_id":"r1"}""")

        let client =
            { Config = PlaidConfig.us "c" "s" PlaidEnvironment.Sandbox "Test Co"
              Http = new System.Net.Http.HttpClient(handler) }

        let! _ = Api.createLinkTokenAsync client "user-42" ct
        let call = Assert.Single handler.Calls
        Assert.Equal<string list>([ "US" ], strings (call.Body.GetProperty "country_codes"))
        Assert.False(call.Body.TryGetProperty "redirect_uri" |> fst)
    }

[<Fact>]
let ``cancelling the caller token throws instead of returning an error`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok "{}")
        use cts = new CancellationTokenSource()
        do! cts.CancelAsync()

        let! _ =
            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                Api.getAccountsAsync (clientWith handler) "access-1" cts.Token :> Task)

        ()
    }
