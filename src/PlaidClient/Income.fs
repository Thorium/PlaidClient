/// Income verification (the Income product, US): income found in bank transactions, and pay stubs from
/// payroll providers. Both belong to a Plaid user (Users). For new US lending integrations Plaid
/// recommends Consumer Report instead; call its /cra/... endpoints with Api.postAsync.
module Plaid.Income

open System
open System.Text.Json
open Plaid.Users

/// Link token for bank income: the user links accounts and Plaid finds income in daysRequested days
/// (1-731) of transactions. Read the result with getBankIncomeAsync after Link's onSuccess; bank income
/// sends no webhook.
let createBankIncomeLinkTokenAsync (client: PlaidClient) (clientUserId: string) (user: UserRef) (daysRequested: int) ct =
    Api.createLinkTokenForProductsAsync
        client
        clientUserId
        [ "income_verification" ]
        [ UserRef.linkField user
          "income_verification",
          box
              {| income_source_types = [ "bank" ]
                 bank_income = {| days_requested = daysRequested |} |} ]
        ct

[<RequireQualifiedAccess>]
type PayrollFlow =
    /// The user signs in to their payroll provider.
    | Digital
    /// The user uploads pay stubs or tax forms.
    | Document

module internal PayrollFlow =

    let code =
        function
        | PayrollFlow.Digital -> "payroll_digital_income"
        | PayrollFlow.Document -> "payroll_document_income"

/// Link token for payroll income through flows ([] offers both). Plaid announces the result with the
/// INCOME USER_INCOME_VERIFICATION webhook.
let createPayrollIncomeLinkTokenAsync
    (client: PlaidClient)
    (clientUserId: string)
    (user: UserRef)
    (flows: PayrollFlow list)
    ct
    =
    Api.createLinkTokenForProductsAsync
        client
        clientUserId
        [ "income_verification" ]
        [ UserRef.linkField user
          "income_verification",
          box
              {| income_source_types = [ "payroll" ]
                 payroll_income = {| flow_types = flows |> List.map PayrollFlow.code |} |} ]
        ct

type MoneyAmount =
    { Amount: decimal
      IsoCurrencyCode: string voption
      UnofficialCurrencyCode: string voption }

type IncomeSource =
    { IncomeSourceId: string
      AccountId: string voption
      Description: string voption
      /// e.g. SALARY, GIG_ECONOMY, RENTAL, BENEFIT_OTHER.
      Category: string voption
      /// WEEKLY, BIWEEKLY, SEMI_MONTHLY, MONTHLY, DAILY or UNKNOWN.
      PayFrequency: string voption
      StartDate: DateTime voption
      EndDate: DateTime voption
      TotalAmount: decimal voption
      TransactionCount: int voption }

type BankIncomeAccount = { Account: Account; Owners: Owner list }

type BankIncomeItem =
    { ItemId: string
      InstitutionId: string voption
      InstitutionName: string voption
      LastUpdated: DateTimeOffset voption
      Accounts: BankIncomeAccount list
      Sources: IncomeSource list }

type BankIncomeSummary =
    { StartDate: DateTime voption
      EndDate: DateTime voption
      TotalAmounts: MoneyAmount list
      IncomeSourcesCount: int voption
      IncomeCategoriesCount: int voption
      IncomeTransactionsCount: int voption }

type BankIncomeReport =
    { BankIncomeId: string
      GeneratedTime: DateTimeOffset voption
      DaysRequested: int voption
      Items: BankIncomeItem list
      Summary: BankIncomeSummary voption
      /// warning_code values, e.g. TRANSACTIONS_UNAVAILABLE, IDENTITY_UNAVAILABLE.
      Warnings: string list
      /// The report as Plaid sent it, including the monthly historical summaries.
      Raw: string }


let private moneyAmount (e: JsonElement) : MoneyAmount =
    { Amount = Json.required "amount" Json.tryDecimal e
      IsoCurrencyCode = Json.tryString "iso_currency_code" e
      UnofficialCurrencyCode = Json.tryString "unofficial_currency_code" e }

let private incomeSource (e: JsonElement) : IncomeSource =
    { IncomeSourceId = Json.requiredString "income_source_id" e
      AccountId = Json.tryString "account_id" e
      Description = Json.tryString "income_description" e
      Category = Json.tryString "income_category" e
      PayFrequency = Json.tryString "pay_frequency" e
      StartDate = Json.tryDate "start_date" e
      EndDate = Json.tryDate "end_date" e
      TotalAmount = Json.tryDecimal "total_amount" e
      TransactionCount = Json.tryInt "transaction_count" e }

let private bankIncomeReport (e: JsonElement) : BankIncomeReport =
    { BankIncomeId = Json.requiredString "bank_income_id" e
      GeneratedTime = Json.tryTimestamp "generated_time" e
      DaysRequested = Json.tryInt "days_requested" e
      Items =
        Json.array "items" e
        |> List.map (fun i ->
            { ItemId = Json.requiredString "item_id" i
              InstitutionId = Json.tryString "institution_id" i
              InstitutionName = Json.tryString "institution_name" i
              LastUpdated = Json.tryTimestamp "last_updated_time" i
              Accounts =
                Json.array "bank_income_accounts" i
                |> List.map (fun a ->
                    { Account = Parse.account a
                      Owners = Json.array "owners" a |> List.map Parse.owner })
              Sources = Json.array "bank_income_sources" i |> List.map incomeSource })
      Summary =
        Json.tryObject "bank_income_summary" e
        |> ValueOption.map (fun s ->
            { StartDate = Json.tryDate "start_date" s
              EndDate = Json.tryDate "end_date" s
              TotalAmounts = Json.array "total_amounts" s |> List.map moneyAmount
              IncomeSourcesCount = Json.tryInt "income_sources_count" s
              IncomeCategoriesCount = Json.tryInt "income_categories_count" s
              IncomeTransactionsCount = Json.tryInt "income_transactions_count" s })
      Warnings = Json.array "warnings" e |> List.choose (Json.tryString "warning_code" >> Json.toOption)
      Raw = e.GetRawText() }

/// The user's bank income reports, newest first; count is how many (Plaid's default is 1).
let getBankIncomeAsync (client: PlaidClient) (user: UserRef) (count: int) ct =
    let body =
        {| user_id = UserRef.userId user
           user_token = UserRef.userToken user
           options = {| count = count |} |}

    Api.postAsync client "/credit/bank_income/get" body (Json.array "bank_income" >> List.map bankIncomeReport) ct

type PayStub =
    { DocumentId: string voption
      EmployerName: string voption
      EmployeeName: string voption
      PayDate: DateTime voption
      StartDate: DateTime voption
      EndDate: DateTime voption
      PayFrequency: string voption
      GrossEarnings: decimal voption
      NetPay: decimal voption
      NetPayYearToDate: decimal voption
      IsoCurrencyCode: string voption }

type PayrollItem =
    { ItemId: string
      InstitutionId: string voption
      InstitutionName: string voption
      /// PROCESSING, PROCESSING_COMPLETE, FAILED, APPROVAL_STATUS_PENDING or UNKNOWN.
      ProcessingStatus: string voption
      PayStubs: PayStub list
      Error: PlaidApiError voption
      /// The item as Plaid sent it, including W-2 and 1099 forms.
      Raw: string }

let private payStub (s: JsonElement) : PayStub =
    let period = Json.tryObject "pay_period_details" s
    let netPay = Json.tryObject "net_pay" s
    let inPeriod reader name = period |> ValueOption.bind (reader name)
    let inNetPay reader name = netPay |> ValueOption.bind (reader name)

    { DocumentId = Json.tryString "document_id" s
      EmployerName = Json.tryObject "employer" s |> ValueOption.bind (Json.tryString "name")
      EmployeeName = Json.tryObject "employee" s |> ValueOption.bind (Json.tryString "name")
      PayDate = inPeriod Json.tryDate "pay_date"
      StartDate = inPeriod Json.tryDate "start_date"
      EndDate = inPeriod Json.tryDate "end_date"
      PayFrequency = inPeriod Json.tryString "pay_frequency"
      GrossEarnings = inPeriod Json.tryDecimal "gross_earnings"
      NetPay = inNetPay Json.tryDecimal "current_amount"
      NetPayYearToDate = inNetPay Json.tryDecimal "ytd_amount"
      IsoCurrencyCode = inNetPay Json.tryString "iso_currency_code" }

let private payrollItem (i: JsonElement) : PayrollItem =
    let incomes = Json.array "payroll_income" i

    { ItemId = Json.requiredString "item_id" i
      InstitutionId = Json.tryString "institution_id" i
      InstitutionName = Json.tryString "institution_name" i
      ProcessingStatus = Json.tryObject "status" i |> ValueOption.bind (Json.tryString "processing_status")
      PayStubs = incomes |> List.collect (Json.array "pay_stubs" >> List.map payStub)
      Error = Parse.embeddedError "error" i
      Raw = i.GetRawText() }

/// The user's payroll items with their pay stubs; itemIds limits it to those items ([] for all).
let getPayrollIncomeAsync (client: PlaidClient) (user: UserRef) (itemIds: string list) ct =
    let body =
        {| user_id = UserRef.userId user
           user_token = UserRef.userToken user
           options = if itemIds.IsEmpty then ValueNone else ValueSome {| item_ids = itemIds |} |}

    Api.postAsync client "/credit/payroll_income/get" body (Json.array "items" >> List.map payrollItem) ct
