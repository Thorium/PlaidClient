module ItemAndInstitutionTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Plaid
open FakePlaid

let private ct = CancellationToken.None

let private expectOk =
    function
    | Ok v -> v
    | Error e -> failwith $"%A{e}"

let private itemJson =
    """{"item_id":"item-1","institution_id":"ins_117650","institution_name":"Royal Bank of Plymouth","webhook":"https://example.test/hook","error":null,"products":["transactions"],"billed_products":["transactions"],"available_products":["balance","identity"],"consented_products":["transactions"],"consent_expiration_time":"2027-04-01T00:00:00Z","update_type":"background"}"""

[<Fact>]
let ``real-time balance sends options only when given and parses last_updated_datetime`` () : Task =
    task {
        let balanceAccount =
            accountJson.Replace("\"unofficial_currency_code\":null}", "\"unofficial_currency_code\":null,\"last_updated_datetime\":\"2026-10-27T08:00:00Z\"}")

        use handler =
            new FakePlaidHandler(fun _ _ -> ok $"""{{"accounts":[{balanceAccount}],"item":{itemJson},"request_id":"r"}}""")

        let client = clientWith handler
        let! all = Api.getBalancesAsync client "access-1" [] ValueNone ct
        let account = Assert.Single (expectOk all).Accounts
        Assert.Equal(ValueSome(DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero)), account.Balances.LastUpdated)

        let since = DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero)
        let! _ = Api.getBalancesAsync client "access-1" [ "acc-1" ] (ValueSome since) ct

        match handler.Calls with
        | [ first; second ] ->
            Assert.Equal("/accounts/balance/get", first.Path)
            Assert.False(first.Body.TryGetProperty "options" |> fst)
            let options = second.Body.GetProperty "options"
            Assert.Equal("acc-1", (options.GetProperty("account_ids")[0]).GetString())
            Assert.Equal(since, options.GetProperty("min_last_updated_datetime").GetDateTimeOffset())
        | calls -> failwith $"%A{calls}"
    }

[<Fact>]
let ``item get parses products, webhook and status`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok
                    $"""{{"item":{itemJson},"status":{{"transactions":{{"last_successful_update":"2026-10-27T06:00:00Z","last_failed_update":null}},"last_webhook":{{"sent_at":"2026-10-27T06:01:00Z","code_sent":"SYNC_UPDATES_AVAILABLE"}}}},"request_id":"r"}}""")

        let! result = Api.getItemAsync (clientWith handler) "access-1" ct
        let details = expectOk result

        Assert.Equal(ValueSome "Royal Bank of Plymouth", details.Item.InstitutionName)
        Assert.Equal(ValueSome "https://example.test/hook", details.Item.Webhook)
        Assert.Equal<string list>([ "balance"; "identity" ], details.Item.AvailableProducts)
        Assert.Equal<string list>([ "transactions" ], details.Item.ConsentedProducts)
        Assert.Equal(ValueSome "background", details.Item.UpdateType)
        Assert.Equal(ValueSome(DateTimeOffset(2026, 10, 27, 6, 0, 0, TimeSpan.Zero)), details.Status.TransactionsLastSuccessfulUpdate)
        Assert.Equal(ValueNone, details.Status.TransactionsLastFailedUpdate)
        Assert.Equal(ValueSome "SYNC_UPDATES_AVAILABLE", details.Status.LastWebhookCode)
        Assert.Equal("/item/get", (Assert.Single handler.Calls).Path)
    }

[<Fact>]
let ``item get without a status object still parses`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok $"""{{"item":{itemJson},"status":null,"request_id":"r"}}""")
        let! result = Api.getItemAsync (clientWith handler) "access-1" ct
        Assert.Equal(ValueNone, (expectOk result).Status.LastWebhookSentAt)
    }

[<Fact>]
let ``webhook update sends the new URL, and an explicit null to remove it`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok $"""{{"item":{itemJson},"request_id":"r"}}""")
        let client = clientWith handler
        let! updated = Api.updateItemWebhookAsync client "access-1" (ValueSome "https://example.test/new") ct
        Assert.Equal("item-1", (expectOk updated).ItemId)
        let! _ = Api.updateItemWebhookAsync client "access-1" ValueNone ct

        match handler.Calls with
        | [ set; remove ] ->
            Assert.Equal("/item/webhook/update", set.Path)
            Assert.Equal("https://example.test/new", set.Body.GetProperty("webhook").GetString())
            Assert.Equal(JsonValueKind.Null, remove.Body.GetProperty("webhook").ValueKind)
        | calls -> failwith $"%A{calls}"
    }

let private institutionJson =
    """{"institution_id":"ins_117650","name":"Royal Bank of Plymouth","products":["transactions","balance"],"country_codes":["GB"],"url":"https://www.example.co.uk","primary_color":"#1f2a44","logo":"iVBORw0KGgo=","oauth":true,"routing_numbers":[]}"""

[<Fact>]
let ``institution by id asks for metadata in the config's countries`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok $"""{{"institution":{institutionJson},"request_id":"r"}}""")
        let! result = Api.getInstitutionAsync (clientWith handler) "ins_117650" ct
        let institution = expectOk result

        Assert.Equal("Royal Bank of Plymouth", institution.Name)
        Assert.True institution.Oauth
        Assert.Equal(ValueSome "iVBORw0KGgo=", institution.Logo)
        Assert.Equal(ValueSome "#1f2a44", institution.PrimaryColor)

        let call = Assert.Single handler.Calls
        Assert.Equal("/institutions/get_by_id", call.Path)
        Assert.Equal("GB", (call.Body.GetProperty("country_codes")[0]).GetString())
        Assert.True(call.Body.GetProperty("options").GetProperty("include_optional_metadata").GetBoolean())
    }

[<Fact>]
let ``institution search filters by products only when given`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok $"""{{"institutions":[{institutionJson}],"request_id":"r"}}""")

        let client = clientWith handler
        let! found = Api.searchInstitutionsAsync client "plymouth" [] ct
        Assert.Equal("ins_117650", (Assert.Single (expectOk found)).InstitutionId)
        let! _ = Api.searchInstitutionsAsync client "plymouth" [ "transactions" ] ct

        match handler.Calls with
        | [ any; filtered ] ->
            Assert.Equal("plymouth", any.Body.GetProperty("query").GetString())
            Assert.False(any.Body.TryGetProperty "products" |> fst)
            Assert.Equal("transactions", (filtered.Body.GetProperty("products")[0]).GetString())
        | calls -> failwith $"%A{calls}"
    }

[<Fact>]
let ``sandbox fire webhook and reset login send their fields and read the flag`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun path _ ->
                match path with
                | "/sandbox/item/fire_webhook" -> ok """{"webhook_fired":true,"request_id":"r"}"""
                | "/sandbox/item/reset_login" -> ok """{"reset_login":true,"request_id":"r"}"""
                | other -> failwith other)

        let client = clientWith handler
        let! fired = Api.sandboxFireWebhookAsync client "access-1" "TRANSACTIONS" "SYNC_UPDATES_AVAILABLE" ct
        let! reset = Api.sandboxResetLoginAsync client "access-1" ct
        Assert.Equal(Ok true, fired)
        Assert.Equal(Ok true, reset)

        let fire = handler.Calls.Head
        Assert.Equal("TRANSACTIONS", fire.Body.GetProperty("webhook_type").GetString())
        Assert.Equal("SYNC_UPDATES_AVAILABLE", fire.Body.GetProperty("webhook_code").GetString())
    }

[<Fact>]
let ``webhook update acknowledgement parses`` () =
    Assert.Equal(
        Ok(Webhook.WebhookUpdateAcknowledged("item-1", ValueSome "https://example.test/new")),
        Webhook.parse
            """{"webhook_type":"ITEM","webhook_code":"WEBHOOK_UPDATE_ACKNOWLEDGED","item_id":"item-1","new_webhook_url":"https://example.test/new","error":null,"environment":"sandbox"}"""
    )

let private apiError status errorType code : PlaidApiError =
    { HttpStatus = status
      ErrorType = errorType
      ErrorCode = code
      ErrorMessage = ""
      DisplayMessage = ValueNone
      RequestId = ValueNone }

[<Theory>]
[<InlineData(429, "RATE_LIMIT_EXCEEDED", "TRANSACTIONS_SYNC_LIMIT", "RetryLater")>]
[<InlineData(429, "RATE_LIMIT_EXCEEDED", "CREDITS_EXHAUSTED", "FixRequest")>]
[<InlineData(500, "API_ERROR", "INTERNAL_SERVER_ERROR", "RetryLater")>]
[<InlineData(400, "INSTITUTION_ERROR", "INSTITUTION_DOWN", "RetryLater")>]
[<InlineData(400, "INSTITUTION_ERROR", "INSTITUTION_NO_LONGER_SUPPORTED", "Unsupported")>]
[<InlineData(400, "ITEM_ERROR", "ITEM_LOGIN_REQUIRED", "UpdateMode")>]
[<InlineData(400, "ITEM_ERROR", "INVALID_CREDENTIALS", "UpdateMode")>]
[<InlineData(400, "ITEM_ERROR", "ITEM_LOCKED", "UserActionAtInstitution")>]
[<InlineData(400, "ITEM_ERROR", "ITEM_NOT_FOUND", "ItemGone")>]
[<InlineData(400, "ITEM_ERROR", "PRODUCT_NOT_READY", "WaitForData")>]
[<InlineData(400, "ITEM_ERROR", "PRODUCTS_NOT_SUPPORTED", "Unsupported")>]
[<InlineData(400, "TRANSACTIONS_ERROR", "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION", "RetryLater")>]
[<InlineData(400, "ASSET_REPORT_ERROR", "ASSET_REPORT_GENERATION_FAILED", "RetryLater")>]
[<InlineData(400, "ASSET_REPORT_ERROR", "PRODUCT_NOT_READY", "WaitForData")>]
[<InlineData(400, "INVALID_REQUEST", "MISSING_FIELDS", "FixRequest")>]
[<InlineData(400, "INVALID_INPUT", "INVALID_API_KEYS", "FixRequest")>]
[<InlineData(503, "SOME_NEW_ERROR", "SOMETHING_NEW", "RetryLater")>]
let ``Plaid errors map to an action`` (status: int, errorType: string, code: string, expected: string) =
    Assert.Equal(expected, $"%A{PlaidApiError.action (apiError status errorType code)}")

[<Fact>]
let ``failures outside Plaid's error object map to an action`` () =
    Assert.Equal(FailureAction.RetryLater, PlaidFailure.action (TransportError(TimeoutException())))
    Assert.Equal(FailureAction.RetryLater, PlaidFailure.action (HttpError(429, "")))
    Assert.Equal(FailureAction.FixRequest, PlaidFailure.action (HttpError(404, "")))
    Assert.Equal(FailureAction.FixRequest, PlaidFailure.action (InvalidResponse "x"))
    Assert.Equal(FailureAction.FixRequest, PlaidFailure.action (UnknownItem "x"))
