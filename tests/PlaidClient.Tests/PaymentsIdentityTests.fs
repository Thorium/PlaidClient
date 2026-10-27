module PaymentsIdentityTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Plaid
open Plaid.Payments
open FakePlaid

let private ct = CancellationToken.None

let private expectOk =
    function
    | Ok v -> v
    | Error e -> failwith $"%A{e}"

let private strings (e: JsonElement) =
    e.EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq

let private itemJson = """{"item_id":"item-1","institution_id":"ins_117650"}"""

[<Fact>]
let ``identity get parses owners per account`` () : Task =
    task {
        let reply =
            $"""{{"accounts":[{accountJson.Replace("}}", "},\"owners\":[{\"names\":[\"Ada Lovelace\"],\"emails\":[{\"data\":\"ada@example.com\",\"primary\":true,\"type\":\"primary\"}],\"phone_numbers\":[],\"addresses\":[]}]}")}],"item":{itemJson},"request_id":"r"}}"""

        use handler = new FakePlaidHandler(fun _ _ -> ok reply)
        let! result = Identity.getAsync (clientWith handler) "access-1" [] ct
        let account = Assert.Single (expectOk result).Accounts
        let owner = Assert.Single account.Owners
        Assert.Equal<string list>([ "Ada Lovelace" ], owner.Names)
        Assert.Equal("ada@example.com", (Assert.Single owner.Emails).Data)
        Assert.False((Assert.Single handler.Calls).Body.TryGetProperty "options" |> fst)
    }

[<Fact>]
let ``identity match sends only the given details and parses null balances and scores`` () : Task =
    task {
        let reply =
            $"""{{"accounts":[{{"account_id":"acc-1","balances":null,"mask":"5555","name":"Current","official_name":null,"type":"depository","subtype":"checking","legal_name":{{"score":90,"is_first_name_or_last_name_match":true,"is_nickname_match":false,"is_business_name_detected":false}},"phone_number":{{"score":null}},"email_address":null,"address":{{"score":100,"is_postal_code_match":true}}}}],"item":{itemJson},"request_id":"r"}}"""

        use handler = new FakePlaidHandler(fun _ _ -> ok reply)

        let user =
            { Identity.MatchUser.ofName "Ada Lovelace" with
                Address = ValueSome { PostalAddress.empty with PostalCode = ValueSome "EC1A 1BB"; Country = ValueSome "GB" } }

        let! result = Identity.matchAsync (clientWith handler) "access-1" user [] ct
        let account = Assert.Single (expectOk result).Accounts
        Assert.Equal(ValueSome 90, account.LegalName |> ValueOption.bind _.Score)
        Assert.Equal(ValueNone, account.PhoneNumber)
        Assert.Equal(ValueNone, account.EmailAddress)
        Assert.Equal(ValueSome true, account.Address |> ValueOption.bind _.IsPostalCodeMatch)
        Assert.Equal(ValueNone, account.Account.Balances.Current)
        Assert.True(Identity.nameMatches Identity.RecommendedThreshold account)
        Assert.False(Identity.nameMatches 95 account)

        let sent = (Assert.Single handler.Calls).Body.GetProperty "user"
        Assert.Equal("Ada Lovelace", sent.GetProperty("legal_name").GetString())
        Assert.False(sent.TryGetProperty "email_address" |> fst)
        Assert.Equal("EC1A 1BB", sent.GetProperty("address").GetProperty("postal_code").GetString())
        Assert.False(sent.GetProperty("address").TryGetProperty "street" |> fst)
    }

let private verificationJson status =
    $"""{{"id":"idv_1","client_user_id":"customer-7","created_at":"2026-10-27T10:00:00Z","completed_at":null,"previous_attempt_id":null,"shareable_url":null,"template":{{"id":"idvtmp_1","version":2}},"user":{{}},"status":"{status}","steps":{{"accept_tos":"success","verify_sms":"success","kyc_check":"success","documentary_verification":"not_applicable","selfie_check":"pending_review","watchlist_screening":"success","risk_check":"success"}},"watchlist_screening_id":"scr_1","request_id":"r"}}"""

[<Fact>]
let ``identity verification create sends the template and prefilled user`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (verificationJson "active"))

        let request =
            { Identity.VerificationRequest.create "idvtmp_1" "customer-7" with
                User =
                    { Identity.VerificationUser.empty with
                        Name = ValueSome { GivenName = "Ada"; FamilyName = "Lovelace" }
                        DateOfBirth = ValueSome(DateTime(1990, 12, 10)) } }

        let! result = Identity.createVerificationAsync (clientWith handler) request ct
        let verification = expectOk result
        Assert.Equal(Identity.VerificationStatus.Active, verification.Status)
        Assert.Equal(Identity.StepStatus.PendingReview, verification.Steps["selfie_check"])
        Assert.Equal(ValueSome "idvtmp_1", verification.TemplateId)
        Assert.Equal(ValueSome "scr_1", verification.WatchlistScreeningId)

        let body = (Assert.Single handler.Calls).Body
        Assert.Equal("idvtmp_1", body.GetProperty("template_id").GetString())
        Assert.True(body.GetProperty("is_idempotent").GetBoolean())
        Assert.Equal("Lovelace", body.GetProperty("user").GetProperty("name").GetProperty("family_name").GetString())
        Assert.Equal("1990-12-10", body.GetProperty("user").GetProperty("date_of_birth").GetString())
        Assert.False(body.GetProperty("user").TryGetProperty "address" |> fst)
    }

[<Fact>]
let ``identity verification get, retry and link token`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun path _ ->
                match path with
                | "/link/token/create" -> ok """{"link_token":"link-1","expiration":"2026-10-27T12:00:00Z","request_id":"r"}"""
                | _ -> ok (verificationJson "failed"))

        let client = clientWith handler
        let! got = Identity.getVerificationAsync client "idv_1" ct
        let! retried = Identity.retryVerificationAsync client "customer-7" "idvtmp_1" Identity.RetryStrategy.Incomplete ct
        let! _ = Identity.createVerificationLinkTokenAsync client "customer-7" "idvtmp_1" (ValueSome "ada@example.com") ct
        Assert.Equal(Identity.VerificationStatus.Failed, (expectOk got).Status)
        Assert.True(Result.isOk retried)

        match handler.Calls with
        | [ get; retry; link ] ->
            Assert.Equal("idv_1", get.Body.GetProperty("identity_verification_id").GetString())
            Assert.Equal("incomplete", retry.Body.GetProperty("strategy").GetString())
            Assert.Equal<string list>([ "identity_verification" ], strings (link.Body.GetProperty "products"))
            Assert.Equal("idvtmp_1", link.Body.GetProperty("identity_verification").GetProperty("template_id").GetString())
            Assert.Equal("ada@example.com", link.Body.GetProperty("user").GetProperty("email_address").GetString())
            Assert.Equal("customer-7", link.Body.GetProperty("user").GetProperty("client_user_id").GetString())
        | calls -> failwith $"%A{calls}"
    }

[<Fact>]
let ``payer account comes from the Auth numbers of the verified account`` () =
    let numbers: Auth.AccountNumbers =
        { Ach = []
          Eft = []
          International = [ { AccountId = "acc-eu"; Iban = "DE89370400440532013000"; Bic = ValueNone } ]
          Bacs = [ { AccountId = "acc-uk"; Account = "55555555"; SortCode = "202015" } ] }

    Assert.Equal(ValueSome(PayerBacs { Account = "55555555"; SortCode = "202015" }), PayerAccount.ofAuth numbers "acc-uk")
    Assert.Equal(ValueSome(PayerIban "DE89370400440532013000"), PayerAccount.ofAuth numbers "acc-eu")
    Assert.Equal(ValueNone, PayerAccount.ofAuth numbers "acc-other")
    let printed = PayerAccount.ofAuth numbers "acc-uk"
    Assert.DoesNotContain("55555555", $"%A{printed}")

[<Fact>]
let ``payment create restricts the payer account and checks the reference first`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok """{"payment_id":"payment-id-sandbox-1","status":"PAYMENT_STATUS_INPUT_NEEDED","request_id":"r"}""")

        let client = clientWith handler

        let request =
            { PaymentRequest.create "recipient-1" "Invoice 42" { Currency = "GBP"; Value = 125.5m } with
                UserId = ValueSome "usr_1"
                PayerAccount = ValueSome(PayerBacs { Account = "55555555"; SortCode = "202015" }) }

        let! created = createPaymentAsync client request ct
        Assert.Equal({ PaymentId = "payment-id-sandbox-1"; Status = PaymentStatus.InputNeeded }, expectOk created)

        let! refused = createPaymentAsync client { request with Reference = "Invoice #42 for October" } ct

        match refused with
        | Error(InvalidArgument _ as failure) -> Assert.Equal(FailureAction.FixRequest, PlaidFailure.action failure)
        | other -> failwith $"%A{other}"

        let body = (Assert.Single handler.Calls).Body
        Assert.Equal(125.5m, body.GetProperty("amount").GetProperty("value").GetDecimal())
        Assert.Equal("usr_1", body.GetProperty("user_id").GetString())
        let options = body.GetProperty "options"
        Assert.Equal("202015", options.GetProperty("bacs").GetProperty("sort_code").GetString())
        Assert.False(options.TryGetProperty "iban" |> fst)
        Assert.False(options.TryGetProperty "request_refund_details" |> fst)
        Assert.False(body.TryGetProperty "schedule" |> fst)
    }

[<Fact>]
let ``recipient account cases send iban, bacs or both`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok """{"recipient_id":"recipient-1","request_id":"r"}""")
        let client = clientWith handler
        let bacs = { Account = "55555555"; SortCode = "202015" }
        let! _ = createRecipientAsync client "Your Company Ltd" (RecipientBacs bacs) ValueNone ct

        let! _ =
            createRecipientAsync
                client
                "Your Company Ltd"
                (RecipientIbanAndBacs("GB33BUKB20201555555555", bacs))
                (ValueSome
                    { Street = [ "1 High St" ]
                      City = "London"
                      PostalCode = "EC1A 1BB"
                      Country = "GB" })
                ct

        match handler.Calls with
        | [ bacsOnly; both ] ->
            Assert.False(bacsOnly.Body.TryGetProperty "iban" |> fst)
            Assert.False(bacsOnly.Body.TryGetProperty "address" |> fst)
            Assert.Equal("202015", bacsOnly.Body.GetProperty("bacs").GetProperty("sort_code").GetString())
            Assert.Equal("GB33BUKB20201555555555", both.Body.GetProperty("iban").GetString())
            Assert.Equal<string list>([ "1 High St" ], strings (both.Body.GetProperty("address").GetProperty "street"))
        | calls -> failwith $"%A{calls}"
    }

[<Fact>]
let ``recipient without iban or bacs is an invalid response, not an empty account`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok """{"recipient_id":"recipient-1","name":"Your Company Ltd","iban":null,"bacs":null,"address":null,"request_id":"r"}""")

        match! getRecipientAsync (clientWith handler) "recipient-1" ct with
        | Error(InvalidResponse message) -> Assert.Contains("neither iban nor bacs", message)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``standing order and scheme are sent as Plaid's codes`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok """{"payment_id":"p-3","status":"PAYMENT_STATUS_INPUT_NEEDED","request_id":"r"}""")

        let request =
            { PaymentRequest.create "recipient-1" "Rent" { Currency = "GBP"; Value = 900m } with
                Scheme = ValueSome Scheme.LocalInstant
                Schedule =
                    ValueSome
                        { Interval = ScheduleInterval.Monthly
                          IntervalExecutionDay = -1
                          StartDate = DateTime(2026, 11, 1)
                          EndDate = ValueNone } }

        let! _ = createPaymentAsync (clientWith handler) request ct
        let body = (Assert.Single handler.Calls).Body
        let schedule = body.GetProperty "schedule"
        Assert.Equal("MONTHLY", schedule.GetProperty("interval").GetString())
        Assert.Equal(-1, schedule.GetProperty("interval_execution_day").GetInt32())
        Assert.Equal("2026-11-01", schedule.GetProperty("start_date").GetString())
        Assert.False(schedule.TryGetProperty "end_date" |> fst)
        Assert.Equal("LOCAL_INSTANT", body.GetProperty("options").GetProperty("scheme").GetString())
        Assert.False(body.GetProperty("options").TryGetProperty "iban" |> fst)
    }

[<Fact>]
let ``a payment schedule without its interval is an invalid response`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok
                    """{"payment_id":"p-1","status":"PAYMENT_STATUS_ESTABLISHED","schedule":{"interval_execution_day":1,"start_date":"2026-11-01"},"request_id":"r"}""")

        match! getPaymentAsync (clientWith handler) "p-1" ct with
        | Error(InvalidResponse message) -> Assert.Contains("interval", message)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``sandbox simulation takes a PaymentStatus`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok """{"old_status":"PAYMENT_STATUS_INITIATED","new_status":"PAYMENT_STATUS_EXECUTED","request_id":"r"}""")

        let! result =
            sandboxSimulatePaymentAsync (clientWith handler) "p-1" PaymentStatus.Executed "https://example.test/hook" ct

        Assert.Equal(Ok PaymentStatus.Executed, result)
        Assert.Equal("PAYMENT_STATUS_EXECUTED", (Assert.Single handler.Calls).Body.GetProperty("status").GetString())
        Assert.Equal("PAYMENT_STATUS_NEW_ONE", PaymentStatus.toString (PaymentStatus.ofString "PAYMENT_STATUS_NEW_ONE"))
    }

[<Fact>]
let ``payment without restrictions sends no options`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok """{"payment_id":"p-2","status":"PAYMENT_STATUS_INPUT_NEEDED","request_id":"r"}""")

        let! _ = createPaymentAsync (clientWith handler) (PaymentRequest.create "recipient-1" "Order 7" { Currency = "EUR"; Value = 10m }) ct
        Assert.False((Assert.Single handler.Calls).Body.TryGetProperty "options" |> fst)
    }

[<Theory>]
[<InlineData("Invoice 42", true)>]
[<InlineData("ABCDEFGHIJKLMNOPQR", true)>]
[<InlineData("ABCDEFGHIJKLMNOPQRS", false)>]
[<InlineData("", false)>]
[<InlineData("Order-7", false)>]
[<InlineData("Käse", false)>]
let ``payment reference rule`` (reference: string, expected: bool) =
    Assert.Equal(expected, isValidReference reference)

[<Fact>]
let ``payment get parses status, payer and error`` () : Task =
    task {
        let reply =
            """{"payment_id":"p-1","amount":{"currency":"GBP","value":125.5},"status":"PAYMENT_STATUS_EXECUTED","recipient_id":"recipient-1","reference":"Invoice 42","adjusted_reference":"INVOICE42","last_status_update":"2026-10-27T10:00:00Z","schedule":null,"refund_details":null,"bacs":{"account":"55555555","sort_code":"202015"},"iban":null,"refund_ids":null,"scheme":"LOCAL_INSTANT","consent_id":null,"end_to_end_id":"E2E1","error":null,"request_id":"r"}"""

        use handler = new FakePlaidHandler(fun _ _ -> ok reply)
        let! result = getPaymentAsync (clientWith handler) "p-1" ct
        let payment = expectOk result
        Assert.Equal(PaymentStatus.Executed, payment.Status)
        Assert.True(PaymentStatus.isSuccessful payment.Status)
        Assert.Equal(ValueSome "INVOICE42", payment.AdjustedReference)
        Assert.Equal(ValueSome { Currency = "GBP"; Value = 125.5m }, payment.Amount)
        Assert.Equal(ValueSome(PayerBacs { Account = "55555555"; SortCode = "202015" }), payment.PayerAccount)
        Assert.Empty payment.RefundIds
        Assert.Equal(ValueNone, payment.Error)
    }

[<Fact>]
let ``payment and consent link tokens carry the id and user`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok """{"link_token":"link-1","expiration":"2026-10-27T12:00:00Z","request_id":"r"}""")

        let client = clientWith handler
        let! _ = createPaymentLinkTokenAsync client "customer-7" (ValueSome "usr_1") "p-1" ct
        let! _ = createConsentLinkTokenAsync client "customer-7" ValueNone "consent-1" ct

        match handler.Calls with
        | [ payment; consent ] ->
            Assert.Equal<string list>([ "payment_initiation" ], strings (payment.Body.GetProperty "products"))
            Assert.Equal("p-1", payment.Body.GetProperty("payment_initiation").GetProperty("payment_id").GetString())
            Assert.Equal("usr_1", payment.Body.GetProperty("user_id").GetString())
            Assert.Equal("consent-1", consent.Body.GetProperty("payment_initiation").GetProperty("consent_id").GetString())
            Assert.False(consent.Body.TryGetProperty "user_id" |> fst)
        | calls -> failwith $"%A{calls}"
    }

[<Fact>]
let ``VRP consent create sends constraints and payer details`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok """{"consent_id":"consent-1","status":"UNAUTHORISED","request_id":"r"}""")

        let request: ConsentRequest =
            { RecipientId = "recipient-1"
              Reference = "Sweep 1"
              UserId = ValueSome "usr_1"
              Type = ConsentType.Sweeping
              Constraints =
                { ValidFrom = ValueSome(DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))
                  ValidTo = ValueNone
                  MaxPaymentAmount = { Currency = "GBP"; Value = 100m }
                  PeriodicAmounts =
                    [ { Amount = { Currency = "GBP"; Value = 300m }
                        Interval = Period.Month
                        Alignment = PeriodAlignment.Calendar } ] }
              Payer = ValueSome { Name = "Ada Lovelace"; Account = PayerIban "GB33BUKB20201555555555" } }

        let! result = createConsentAsync (clientWith handler) request ct
        Assert.Equal(("consent-1", ConsentStatus.Unauthorised), expectOk result)

        let body = (Assert.Single handler.Calls).Body
        Assert.Equal("SWEEPING", body.GetProperty("type").GetString())
        let constraints = body.GetProperty "constraints"
        Assert.True(constraints.GetProperty("valid_date_time").TryGetProperty "from" |> fst)
        Assert.False(constraints.GetProperty("valid_date_time").TryGetProperty "to" |> fst)
        Assert.Equal("MONTH", (constraints.GetProperty("periodic_amounts")[0]).GetProperty("interval").GetString())
        let payer = body.GetProperty "payer_details"
        Assert.Equal("GB33BUKB20201555555555", payer.GetProperty("account_numbers").GetProperty("iban").GetString())
        Assert.False(payer.GetProperty("account_numbers").TryGetProperty "bacs" |> fst)
    }

[<Fact>]
let ``VRP payment execute sends the idempotency key`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok """{"payment_id":"p-9","status":"PAYMENT_STATUS_INITIATED","request_id":"r"}""")

        let! result =
            executeConsentPaymentAsync (clientWith handler) "consent-1" { Currency = "GBP"; Value = 20m } "sweep-2026-11-01" ValueNone ct

        Assert.Equal(PaymentStatus.Initiated, (expectOk result).Status)
        let body = (Assert.Single handler.Calls).Body
        Assert.Equal("sweep-2026-11-01", body.GetProperty("idempotency_key").GetString())
        Assert.False(body.TryGetProperty "reference" |> fst)
    }

[<Fact>]
let ``payment, consent and identity verification webhooks parse`` () =
    match
        Webhook.parse
            """{"webhook_type":"PAYMENT_INITIATION","webhook_code":"PAYMENT_STATUS_UPDATE","payment_id":"p-1","new_payment_status":"PAYMENT_STATUS_INSUFFICIENT_FUNDS","old_payment_status":"PAYMENT_STATUS_AUTHORISING","original_reference":"Invoice 42","adjusted_reference":null,"timestamp":"2026-10-27T10:00:00Z","error":null,"environment":"sandbox"}"""
    with
    | Ok(Webhook.PaymentStatusUpdate("p-1", newStatus, ValueSome PaymentStatus.Authorising, ValueSome timestamp, ValueNone)) ->
        Assert.True(PaymentStatus.isFailed newStatus)
        Assert.Equal(DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero), timestamp)
    | other -> failwith $"%A{other}"

    Assert.Equal(
        Ok(
            Webhook.ConsentStatusUpdate(
                "consent-1",
                ConsentStatus.Authorised,
                ValueSome ConsentStatus.Unauthorised,
                ValueSome(DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero)),
                ValueNone
            )
        ),
        Webhook.parse
            """{"webhook_type":"PAYMENT_INITIATION","webhook_code":"CONSENT_STATUS_UPDATE","consent_id":"consent-1","old_status":"UNAUTHORISED","new_status":"AUTHORISED","timestamp":"2026-10-27T10:00:00Z","error":null,"environment":"sandbox"}"""
    )

    Assert.Equal(
        Ok(Webhook.IdentityVerificationUpdated("idv_1", Identity.VerificationChange.StatusUpdated)),
        Webhook.parse
            """{"webhook_type":"IDENTITY_VERIFICATION","webhook_code":"STATUS_UPDATED","identity_verification_id":"idv_1","environment":"sandbox"}"""
    )
