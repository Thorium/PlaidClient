module UsProductsTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Plaid
open Plaid.Users
open FakePlaid

let private ct = CancellationToken.None

let private expectOk =
    function
    | Ok v -> v
    | Error e -> failwith $"%A{e}"

let private strings (e: JsonElement) =
    e.EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq

let private itemJson =
    """{"item_id":"item-1","institution_id":"ins_1","products":["auth"],"billed_products":["auth"]}"""

let private ownerJson =
    """{"names":["Alberta Bobbeth Charleson"],"emails":[{"data":"accountholder0@example.com","primary":true,"type":"primary"}],"phone_numbers":[{"data":"1112223333","primary":false,"type":"home"}],"addresses":[{"data":{"city":"Malakoff","region":"NY","street":"2992 Cameron Road","postal_code":"14236","country":"US"},"primary":true}]}"""

[<Fact>]
let ``user create sends the new identity shape and returns user_id`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok """{"user_id":"usr_1","request_id":"r"}""")

        let identity =
            { UserIdentity.create "Ada" "Lovelace" with
                DateOfBirth = ValueSome(DateTime(1990, 12, 10))
                Emails = [ { Data = "ada@example.com"; Primary = true } ] }

        let! result = Users.createAsync (clientWith handler) "customer-7" (ValueSome identity) ct
        let created = expectOk result
        Assert.Equal("usr_1", created.UserId)
        Assert.Equal(UserId "usr_1", CreatedUser.toUserRef created)

        let body = (Assert.Single handler.Calls).Body
        let sent = body.GetProperty "identity"
        Assert.Equal("customer-7", body.GetProperty("client_user_id").GetString())
        Assert.Equal("Ada", sent.GetProperty("name").GetProperty("given_name").GetString())
        Assert.Equal("1990-12-10", sent.GetProperty("date_of_birth").GetString())
        Assert.True((sent.GetProperty("emails")[0]).GetProperty("primary").GetBoolean())
        Assert.False(sent.TryGetProperty "phone_numbers" |> fst, "empty lists are left out")
    }

[<Fact>]
let ``legacy user create sends consumer_report_user_identity and keeps the token`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok """{"user_id":"usr_2","user_token":"user-sandbox-1","request_id":"r"}""")

        let identity: LegacyUserIdentity =
            { FirstName = "Ada"
              LastName = "Lovelace"
              PhoneNumbers = [ "+15555550123" ]
              Emails = [ "ada@example.com" ]
              DateOfBirth = DateTime(1990, 12, 10)
              Street = "1 Main St"
              City = "Springfield"
              Region = "IL"
              PostalCode = "62701"
              Country = "US" }

        let! result = Users.createLegacyAsync (clientWith handler) "customer-7" identity ct
        let created = expectOk result
        Assert.Equal(UserToken "user-sandbox-1", CreatedUser.toUserRef created)
        Assert.DoesNotContain("user-sandbox-1", string created)
        Assert.DoesNotContain("user-sandbox-1", $"%A{CreatedUser.toUserRef created}")

        let sent = (Assert.Single handler.Calls).Body.GetProperty "consumer_report_user_identity"
        Assert.Equal("62701", sent.GetProperty("primary_address").GetProperty("postal_code").GetString())
        Assert.Equal<string list>([ "+15555550123" ], strings (sent.GetProperty "phone_numbers"))
    }

[<Fact>]
let ``auth numbers parse per scheme and print masked`` () : Task =
    task {
        let numbers =
            """{"ach":[{"account_id":"acc-1","account":"1111222233330000","routing":"011401533","wire_routing":"021000021"}],"eft":[],"international":[{"account_id":"acc-2","iban":"GB33BUKB20201555555555","bic":"BUKBGB22"}],"bacs":[{"account_id":"acc-2","account":"55555555","sort_code":"202015"}]}"""

        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok $"""{{"accounts":[{accountJson}],"numbers":{numbers},"item":{itemJson},"request_id":"r"}}""")

        let! result = Auth.getAsync (clientWith handler) "access-1" [ "acc-1" ] ct
        let auth = expectOk result
        let ach = Assert.Single auth.Numbers.Ach
        Assert.Equal("011401533", ach.Routing)
        Assert.Equal("202015", (Assert.Single auth.Numbers.Bacs).SortCode)
        Assert.Equal(ValueSome "BUKBGB22", (Assert.Single auth.Numbers.International).Bic)
        Assert.Empty auth.Numbers.Eft

        let printed = $"%A{ach}" + string auth.Numbers.Bacs.Head
        Assert.DoesNotContain("1111222233330000", printed)
        Assert.Contains("****0000", printed)

        let call = Assert.Single handler.Calls
        Assert.Equal("/auth/get", call.Path)
        Assert.Equal<string list>([ "acc-1" ], strings (call.Body.GetProperty("options").GetProperty "account_ids"))
    }

[<Fact>]
let ``processor token for Checkbook`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok """{"processor_token":"processor-sandbox-1","request_id":"r"}""")
        let! result = Auth.createProcessorTokenAsync (clientWith handler) "access-1" "acc-1" Auth.Processor.Checkbook ct
        Assert.Equal(Ok "processor-sandbox-1", result)
        let body = (Assert.Single handler.Calls).Body
        Assert.Equal("checkbook", body.GetProperty("processor").GetString())
        Assert.Equal("acc-1", body.GetProperty("account_id").GetString())
    }

[<Fact>]
let ``asset report create sends options only when set and hides the token`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok """{"asset_report_token":"assets-sandbox-1","asset_report_id":"report-1","request_id":"r"}""")

        let client = clientWith handler
        let! plain = Assets.createReportAsync client [ "access-1" ] 180 Assets.CreateOptions.empty ct

        let options =
            { Assets.CreateOptions.empty with
                ClientReportId = ValueSome "loan-42"
                Webhook = ValueSome "https://example.test/assets"
                User =
                    ValueSome
                        { Assets.ReportUser.empty with
                            FirstName = ValueSome "Ada"
                            Ssn = ValueSome "123-45-6789" } }

        let! _ = Assets.createReportAsync client [ "access-1"; "access-2" ] 61 options ct
        let created = expectOk plain
        Assert.Equal("report-1", created.AssetReportId)
        Assert.DoesNotContain("assets-sandbox-1", string created)
        Assert.DoesNotContain("123-45-6789", $"%A{options.User}")

        match handler.Calls with
        | [ first; second ] ->
            Assert.False(first.Body.TryGetProperty "options" |> fst)
            Assert.Equal(180, first.Body.GetProperty("days_requested").GetInt32())
            let sent = second.Body.GetProperty "options"
            Assert.Equal("loan-42", sent.GetProperty("client_report_id").GetString())
            Assert.Equal("123-45-6789", sent.GetProperty("user").GetProperty("ssn").GetString())
            Assert.False(sent.GetProperty("user").TryGetProperty "last_name" |> fst)
            Assert.False(sent.TryGetProperty "add_ons" |> fst)
        | calls -> failwith $"%A{calls}"
    }

let private reportJson =
    $"""{{"report":{{"asset_report_id":"report-1","client_report_id":"loan-42","date_generated":"2026-10-27T10:00:00Z","days_requested":61,"items":[{{"item_id":"item-1","institution_id":"ins_1","institution_name":"First Platypus Bank","date_last_updated":"2026-10-27T09:00:00Z","accounts":[{{"account_id":"acc-1","balances":{{"available":100,"current":110,"limit":null,"margin_amount":null,"iso_currency_code":"USD","unofficial_currency_code":null}},"days_available":61,"historical_balances":[{{"date":"2026-10-26","current":110,"iso_currency_code":"USD","unofficial_currency_code":null}}],"mask":"0000","name":"Plaid Checking","official_name":null,"type":"depository","subtype":"checking","ownership_type":"individual","owners":[{ownerJson}],"transactions":[{{"transaction_id":"tx-1","account_id":"acc-1","amount":89.4,"date":"2026-10-20","pending":false,"original_description":"SparkFun","iso_currency_code":"USD","unofficial_currency_code":null}}]}}]}}],"user":{{"client_user_id":"customer-7"}}}},"warnings":[{{"warning_type":"ASSET_REPORT_WARNING","warning_code":"OWNERS_UNAVAILABLE"}}],"request_id":"r"}}"""

[<Fact>]
let ``asset report get parses items, owners, balances and transactions`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok reportJson)

        let! result =
            Assets.getReportAsync
                (clientWith handler)
                "assets-sandbox-1"
                { Assets.GetOptions.defaults with DaysToInclude = ValueSome 30 }
                ct

        let report = expectOk result
        Assert.Equal(ValueSome "loan-42", report.ClientReportId)
        Assert.Equal<string list>([ "OWNERS_UNAVAILABLE" ], report.Warnings)
        let account = (Assert.Single report.Items).Accounts |> Assert.Single
        Assert.Equal(ValueSome 61, account.DaysAvailable)
        Assert.Equal("Alberta Bobbeth Charleson", (Assert.Single account.Owners).Names.Head)
        Assert.Equal(ValueSome "14236", account.Owners.Head.Addresses.Head.Data.PostalCode)
        Assert.Equal(110m, (Assert.Single account.HistoricalBalances).Current)
        Assert.Equal(89.4m, (Assert.Single account.Transactions).Amount)

        let body = (Assert.Single handler.Calls).Body
        Assert.Equal(30, body.GetProperty("options").GetProperty("days_to_include").GetInt32())
        Assert.False(body.GetProperty("include_insights").GetBoolean())
    }

[<Fact>]
let ``asset report PDF comes back as bytes`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok "%PDF-1.7 fake")
        let! result = Assets.getReportPdfAsync (clientWith handler) "assets-sandbox-1" ct
        Assert.Equal("%PDF-1.7 fake", Text.Encoding.UTF8.GetString(expectOk result))
    }

[<Fact>]
let ``asset report not ready maps to WaitForData`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                400,
                """{"error_type":"ASSET_REPORT_ERROR","error_code":"PRODUCT_NOT_READY","error_message":"m","display_message":null,"request_id":"r"}""")

        match! Assets.getReportAsync (clientWith handler) "t" Assets.GetOptions.defaults ct with
        | Error failure -> Assert.Equal(FailureAction.WaitForData, PlaidFailure.action failure)
        | Ok _ -> failwith "expected an error"
    }

[<Fact>]
let ``income link tokens name the user and the source`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok """{"link_token":"link-1","expiration":"2026-10-27T12:00:00Z","request_id":"r"}""")

        let client = clientWith handler
        let! _ = Income.createBankIncomeLinkTokenAsync client "customer-7" (UserToken "user-sandbox-1") 180 ct
        let! _ = Income.createPayrollIncomeLinkTokenAsync client "customer-7" (UserId "usr_1") [ Income.PayrollFlow.Digital ] ct

        match handler.Calls with
        | [ bank; payroll ] ->
            Assert.Equal("user-sandbox-1", bank.Body.GetProperty("user_token").GetString())
            Assert.Equal<string list>([ "income_verification" ], strings (bank.Body.GetProperty "products"))
            let bankIncome = bank.Body.GetProperty "income_verification"
            Assert.Equal<string list>([ "bank" ], strings (bankIncome.GetProperty "income_source_types"))
            Assert.Equal(180, bankIncome.GetProperty("bank_income").GetProperty("days_requested").GetInt32())
            Assert.Equal("usr_1", payroll.Body.GetProperty("user_id").GetString())

            Assert.Equal<string list>(
                [ "payroll_digital_income" ],
                strings (payroll.Body.GetProperty("income_verification").GetProperty("payroll_income").GetProperty "flow_types")
            )
        | calls -> failwith $"%A{calls}"
    }

[<Fact>]
let ``bank income parses sources and summary`` () : Task =
    task {
        let reply =
            $"""{{"bank_income":[{{"bank_income_id":"bi-1","generated_time":"2026-10-27T10:00:00Z","days_requested":180,"items":[{{"item_id":"item-1","institution_id":"ins_1","institution_name":"First Platypus Bank","last_updated_time":"2026-10-27T09:00:00Z","bank_income_accounts":[{{"account_id":"acc-1","mask":"0000","name":"Checking","official_name":null,"type":"depository","subtype":"checking","owners":[{ownerJson}]}}],"bank_income_sources":[{{"income_source_id":"src-1","account_id":"acc-1","income_description":"PLAID INC DIRECT DEP","income_category":"SALARY","pay_frequency":"BIWEEKLY","start_date":"2026-05-01","end_date":"2026-10-17","total_amount":28000.5,"transaction_count":12,"historical_summary":[]}}]}}],"bank_income_summary":{{"start_date":"2026-05-01","end_date":"2026-10-17","total_amounts":[{{"amount":28000.5,"iso_currency_code":"USD","unofficial_currency_code":null}}],"income_sources_count":1,"income_categories_count":1,"income_transactions_count":12,"historical_summary":[]}},"warnings":[]}}],"request_id":"r"}}"""

        use handler = new FakePlaidHandler(fun _ _ -> ok reply)
        let! result = Income.getBankIncomeAsync (clientWith handler) (UserToken "user-sandbox-1") 1 ct
        let report = Assert.Single(expectOk result)
        let source = (Assert.Single report.Items).Sources |> Assert.Single
        Assert.Equal(ValueSome "SALARY", source.Category)
        Assert.Equal(ValueSome 28000.5m, source.TotalAmount)
        Assert.Equal(ValueSome 12, report.Summary |> ValueOption.bind _.IncomeTransactionsCount)
        Assert.Equal("Checking", report.Items.Head.Accounts.Head.Account.Name)

        let body = (Assert.Single handler.Calls).Body
        Assert.Equal("user-sandbox-1", body.GetProperty("user_token").GetString())
        Assert.Equal(1, body.GetProperty("options").GetProperty("count").GetInt32())
    }

[<Fact>]
let ``payroll income flattens pay stubs and leaves options out for all items`` () : Task =
    task {
        let reply =
            """{"items":[{"item_id":"item-p","institution_id":"ins_90","institution_name":"ADP","accounts":[],"payroll_income":[{"account_id":null,"pay_stubs":[{"document_id":"doc-1","employer":{"name":"Plaid Inc","address":null},"employee":{"name":"Ada Lovelace"},"net_pay":{"current_amount":2350.12,"ytd_amount":40001.5,"iso_currency_code":"USD"},"pay_period_details":{"pay_date":"2026-10-15","start_date":"2026-10-01","end_date":"2026-10-14","gross_earnings":3200,"pay_frequency":"BIWEEKLY"}}],"w2s":[{}],"form1099s":[]}],"status":{"processing_status":"PROCESSING_COMPLETE"},"error":null}],"request_id":"r"}"""

        use handler = new FakePlaidHandler(fun _ _ -> ok reply)
        let! result = Income.getPayrollIncomeAsync (clientWith handler) (UserId "usr_1") [] ct
        let item = Assert.Single(expectOk result)
        let stub = Assert.Single item.PayStubs
        Assert.Equal(ValueSome "PROCESSING_COMPLETE", item.ProcessingStatus)
        Assert.Equal(ValueSome 2350.12m, stub.NetPay)
        Assert.Equal(ValueSome 3200m, stub.GrossEarnings)
        Assert.Equal(ValueSome(DateTime(2026, 10, 15)), stub.PayDate)
        Assert.Equal(ValueSome "Plaid Inc", stub.EmployerName)
        Assert.Equal(ValueNone, item.Error)
        Assert.False((Assert.Single handler.Calls).Body.TryGetProperty "options" |> fst)
    }

[<Fact>]
let ``assets and income webhooks parse`` () =
    Assert.Equal(
        Ok(Webhook.AssetReportReady("report-1", ValueSome "FULL", ValueSome "usr_1")),
        Webhook.parse
            """{"webhook_type":"ASSETS","webhook_code":"PRODUCT_READY","asset_report_id":"report-1","report_type":"FULL","user_id":"usr_1","environment":"sandbox"}"""
    )

    match
        Webhook.parse
            """{"webhook_type":"ASSETS","webhook_code":"ERROR","asset_report_id":"report-1","error":{"error_type":"ASSET_REPORT_ERROR","error_code":"ASSET_REPORT_GENERATION_FAILED","error_message":"m","display_message":null,"request_id":"r"},"environment":"sandbox"}"""
    with
    | Ok(Webhook.AssetReportFailed("report-1", ValueSome e)) -> Assert.Equal("ASSET_REPORT_GENERATION_FAILED", e.ErrorCode)
    | other -> failwith $"%A{other}"

    Assert.Equal(
        Ok(Webhook.IncomeVerification(ValueSome "item-p", ValueSome "usr_1", ValueSome "VERIFICATION_STATUS_PROCESSING_COMPLETE")),
        Webhook.parse
            """{"webhook_type":"INCOME","webhook_code":"USER_INCOME_VERIFICATION","item_id":"item-p","user_id":"usr_1","verification_status":"VERIFICATION_STATUS_PROCESSING_COMPLETE","environment":"sandbox"}"""
    )
