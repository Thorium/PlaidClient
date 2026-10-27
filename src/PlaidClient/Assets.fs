/// Asset reports (the Assets product): balances, history and owners across a user's items, for lending.
/// Reports are built in the background; Plaid announces them with the ASSETS PRODUCT_READY webhook.
module Plaid.Assets

open System
open System.Text.Json

/// The borrower as the report should name them. Printing hides the SSN.
[<StructuredFormatDisplay("{Display}")>]
type ReportUser =
    { ClientUserId: string voption
      FirstName: string voption
      MiddleName: string voption
      LastName: string voption
      /// "ddd-dd-dddd"; with first and last name, required for Fannie Mae Day 1 Certainty.
      Ssn: string voption
      /// E.164.
      PhoneNumber: string voption
      Email: string voption }

    member this.Display =
        $"ReportUser {{ ClientUserId = %A{this.ClientUserId}; FirstName = %A{this.FirstName}; LastName = %A{this.LastName}; Ssn = *** }}"

    override this.ToString() = this.Display

module ReportUser =

    let empty =
        { ClientUserId = ValueNone
          FirstName = ValueNone
          MiddleName = ValueNone
          LastName = ValueNone
          Ssn = ValueNone
          PhoneNumber = ValueNone
          Email = ValueNone }

[<RequireQualifiedAccess>]
type AddOn =
    /// Investment holdings and transactions; the item must have been linked with investments.
    | Investments
    /// A fast report (identity and balances only) alongside the full one, at no extra charge.
    | FastAssets

module internal AddOn =

    let code =
        function
        | AddOn.Investments -> "investments"
        | AddOn.FastAssets -> "fast_assets"

type CreateOptions =
    { /// Your own id for the report, returned in it.
      ClientReportId: string voption
      /// Receives PRODUCT_READY / ERROR for this report.
      Webhook: string voption
      User: ReportUser voption
      AddOns: AddOn list }

module CreateOptions =

    let empty =
        { ClientReportId = ValueNone
          Webhook = ValueNone
          User = ValueNone
          AddOns = [] }

/// Identifies a report. The token grants access to the report's data; printing hides it.
[<StructuredFormatDisplay("{Display}")>]
type ReportRef =
    { AssetReportId: string
      AssetReportToken: string }

    member this.Display = $"ReportRef {{ AssetReportId = %s{this.AssetReportId}; AssetReportToken = *** }}"
    override this.ToString() = this.Display

let private reportRef (e: JsonElement) : ReportRef =
    { AssetReportId = Json.requiredString "asset_report_id" e
      AssetReportToken = Json.requiredString "asset_report_token" e }

let private reportUser (user: ReportUser) =
    {| client_user_id = user.ClientUserId
       first_name = user.FirstName
       middle_name = user.MiddleName
       last_name = user.LastName
       ssn = user.Ssn
       phone_number = user.PhoneNumber
       email = user.Email |}

let private createOptions (options: CreateOptions) =
    if options = CreateOptions.empty then
        ValueNone
    else
        ValueSome
            {| client_report_id = options.ClientReportId
               webhook = options.Webhook
               user = options.User |> ValueOption.map reportUser
               add_ons = options.AddOns |> List.map AddOn.code |}

/// Starts a report over the items of accessTokens (1-99) covering daysRequested days (0-731).
let createReportAsync (client: PlaidClient) (accessTokens: string list) (daysRequested: int) (options: CreateOptions) ct =
    let body =
        {| access_tokens = accessTokens
           days_requested = daysRequested
           options = createOptions options |}

    Api.postAsync client "/asset_report/create" body reportRef ct

type HistoricalBalance =
    { Date: DateTime
      Current: decimal
      IsoCurrencyCode: string voption
      UnofficialCurrencyCode: string voption }

type ReportTransaction =
    { TransactionId: string
      AccountId: string
      /// Positive when money leaves the account.
      Amount: decimal
      Date: DateTime
      Pending: bool
      OriginalDescription: string voption
      MerchantName: string voption
      IsoCurrencyCode: string voption
      UnofficialCurrencyCode: string voption }

type ReportAccount =
    { Account: Account
      DaysAvailable: int voption
      /// "individual", "joint", "association" or "trust".
      OwnershipType: string voption
      Owners: Owner list
      HistoricalBalances: HistoricalBalance list
      Transactions: ReportTransaction list }

type ReportItem =
    { ItemId: string
      InstitutionId: string voption
      InstitutionName: string voption
      DateLastUpdated: DateTimeOffset voption
      Accounts: ReportAccount list }

type AssetReport =
    { AssetReportId: string
      ClientReportId: string voption
      DateGenerated: DateTimeOffset voption
      DaysRequested: int voption
      Items: ReportItem list
      /// warning_code values, e.g. OWNERS_UNAVAILABLE, TRANSACTIONS_UNAVAILABLE.
      Warnings: string list
      /// The whole reply as Plaid sent it, including insights and investments.
      Raw: string }


let private reportAccount (a: JsonElement) : ReportAccount =
    { Account = Parse.account a
      DaysAvailable = Json.tryInt "days_available" a
      OwnershipType = Json.tryString "ownership_type" a
      Owners = Json.array "owners" a |> List.map Parse.owner
      HistoricalBalances =
        Json.array "historical_balances" a
        |> List.map (fun b ->
            { Date = Json.required "date" Json.tryDate b
              Current = Json.required "current" Json.tryDecimal b
              IsoCurrencyCode = Json.tryString "iso_currency_code" b
              UnofficialCurrencyCode = Json.tryString "unofficial_currency_code" b })
      Transactions =
        Json.array "transactions" a
        |> List.map (fun t ->
            { TransactionId = Json.requiredString "transaction_id" t
              AccountId = Json.requiredString "account_id" t
              Amount = Json.required "amount" Json.tryDecimal t
              Date = Json.required "date" Json.tryDate t
              Pending = Json.tryBool "pending" t |> ValueOption.defaultValue false
              OriginalDescription = Json.tryString "original_description" t
              MerchantName = Json.tryString "merchant_name" t
              IsoCurrencyCode = Json.tryString "iso_currency_code" t
              UnofficialCurrencyCode = Json.tryString "unofficial_currency_code" t }) }

let private assetReport (e: JsonElement) : AssetReport =
    let report = Json.required "report" Json.tryObject e

    { AssetReportId = Json.requiredString "asset_report_id" report
      ClientReportId = Json.tryString "client_report_id" report
      DateGenerated = Json.tryTimestamp "date_generated" report
      DaysRequested = Json.tryInt "days_requested" report
      Items =
        Json.array "items" report
        |> List.map (fun i ->
            { ItemId = Json.requiredString "item_id" i
              InstitutionId = Json.tryString "institution_id" i
              InstitutionName = Json.tryString "institution_name" i
              DateLastUpdated = Json.tryTimestamp "date_last_updated" i
              Accounts = Json.array "accounts" i |> List.map reportAccount })
      Warnings = Json.array "warnings" e |> List.choose (Json.tryString "warning_code" >> Json.toOption)
      Raw = e.GetRawText() }

[<Struct>]
type GetOptions =
    { /// Adds income, risk and merchant insights; the report must have been created with insights.
      IncludeInsights: bool
      /// The fast report (identity and balances only), when created with the "fast_assets" add-on.
      FastReport: bool
      /// Limits the transactions returned to this many most recent days.
      DaysToInclude: int voption }

module GetOptions =

    let defaults =
        { IncludeInsights = false
          FastReport = false
          DaysToInclude = ValueNone }

/// The report once PRODUCT_READY has arrived; before that Plaid answers PRODUCT_NOT_READY
/// (FailureAction.WaitForData).
let getReportAsync (client: PlaidClient) (reportToken: string) (options: GetOptions) ct =
    let body =
        {| asset_report_token = reportToken
           include_insights = options.IncludeInsights
           fast_report = options.FastReport
           options = options.DaysToInclude |> ValueOption.map (fun days -> {| days_to_include = days |}) |}

    Api.postAsync client "/asset_report/get" body assetReport ct

/// The report as a PDF file.
let getReportPdfAsync (client: PlaidClient) (reportToken: string) ct =
    Api.postForBytesAsync client "/asset_report/pdf/get" {| asset_report_token = reportToken |} ct

/// A new report with fresh data and the original settings; daysRequested overrides the original.
let refreshReportAsync (client: PlaidClient) (reportToken: string) (daysRequested: int voption) ct =
    let body =
        {| asset_report_token = reportToken
           days_requested = daysRequested |}

    Api.postAsync client "/asset_report/refresh" body reportRef ct

/// A new report without the given accounts, e.g. ones the borrower does not want shared. Not billed.
let filterReportAsync (client: PlaidClient) (reportToken: string) (accountIdsToExclude: string list) ct =
    let body =
        {| asset_report_token = reportToken
           account_ids_to_exclude = accountIdsToExclude |}

    Api.postAsync client "/asset_report/filter" body reportRef ct

/// Deletes the report; the items it was built from are not affected.
let removeReportAsync (client: PlaidClient) (reportToken: string) ct =
    Api.postAsync client "/asset_report/remove" {| asset_report_token = reportToken |} (Json.required "removed" Json.tryBool) ct
