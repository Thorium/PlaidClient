/// Account and routing numbers (the Auth product), and processor tokens that hand an account to a
/// payment partner such as Checkbook or Stripe without sharing the numbers.
module Plaid.Auth

open System.Text.Json

/// US numbers. Printing shows only the last four digits of the account number.
[<StructuredFormatDisplay("{Display}")>]
type AchNumbers =
    { AccountId: string
      Account: string
      Routing: string
      WireRouting: string voption }

    member this.Display =
        $"AchNumbers {{ AccountId = %s{this.AccountId}; Account = %s{Mask.number this.Account}; Routing = %s{this.Routing} }}"

    override this.ToString() = this.Display

/// Canadian numbers. Printing shows only the last four digits of the account number.
[<StructuredFormatDisplay("{Display}")>]
type EftNumbers =
    { AccountId: string
      Account: string
      Institution: string
      Branch: string }

    member this.Display =
        $"EftNumbers {{ AccountId = %s{this.AccountId}; Account = %s{Mask.number this.Account}; Branch = %s{this.Branch} }}"

    override this.ToString() = this.Display

/// European IBAN/BIC. Printing shows only the last four characters of the IBAN.
[<StructuredFormatDisplay("{Display}")>]
type InternationalNumbers =
    { AccountId: string
      Iban: string
      Bic: string voption }

    member this.Display =
        $"InternationalNumbers {{ AccountId = %s{this.AccountId}; Iban = %s{Mask.number this.Iban} }}"

    override this.ToString() = this.Display

/// UK account number and sort code. Printing shows only the last four digits of the account number.
[<StructuredFormatDisplay("{Display}")>]
type BacsNumbers =
    { AccountId: string
      Account: string
      SortCode: string }

    member this.Display =
        $"BacsNumbers {{ AccountId = %s{this.AccountId}; Account = %s{Mask.number this.Account}; SortCode = %s{this.SortCode} }}"

    override this.ToString() = this.Display

/// Numbers by scheme. An account can appear under several; credit, loan and investment accounts have none.
type AccountNumbers =
    { Ach: AchNumbers list
      Eft: EftNumbers list
      International: InternationalNumbers list
      Bacs: BacsNumbers list }

type AuthResult =
    { Item: ItemInfo
      Accounts: Account list
      Numbers: AccountNumbers }

let private authResult (e: JsonElement) : AuthResult =
    let numbers = Json.required "numbers" Json.tryObject e

    { Item = Parse.item e
      Accounts = Json.array "accounts" e |> List.map Parse.account
      Numbers =
        { Ach =
            Json.array "ach" numbers
            |> List.map (fun n ->
                { AccountId = Json.requiredString "account_id" n
                  Account = Json.requiredString "account" n
                  Routing = Json.requiredString "routing" n
                  WireRouting = Json.tryString "wire_routing" n })
          Eft =
            Json.array "eft" numbers
            |> List.map (fun n ->
                { AccountId = Json.requiredString "account_id" n
                  Account = Json.requiredString "account" n
                  Institution = Json.requiredString "institution" n
                  Branch = Json.requiredString "branch" n })
          International =
            Json.array "international" numbers
            |> List.map (fun n ->
                { AccountId = Json.requiredString "account_id" n
                  Iban = Json.requiredString "iban" n
                  Bic = Json.tryString "bic" n })
          Bacs =
            Json.array "bacs" numbers
            |> List.map (fun n ->
                { AccountId = Json.requiredString "account_id" n
                  Account = Json.requiredString "account" n
                  SortCode = Json.requiredString "sort_code" n }) } }

/// Account numbers for the item's accounts; accountIds limits it to those accounts ([] for all).
let getAsync (client: PlaidClient) (accessToken: string) (accountIds: string list) ct =
    let body =
        {| access_token = accessToken
           options = Request.accountOptions accountIds |}

    Api.postAsync client "/auth/get" body authResult ct

/// Processor names for createProcessorTokenAsync. Plaid supports many more; any name it lists works.
module Processor =

    [<Literal>]
    let Checkbook = "checkbook"

    [<Literal>]
    let Dwolla = "dwolla"

    [<Literal>]
    let ModernTreasury = "modern_treasury"

    [<Literal>]
    let Adyen = "adyen"

    [<Literal>]
    let Checkout = "checkout"

    [<Literal>]
    let Moov = "moov"

/// A token that lets processor (e.g. Processor.Checkbook) read the account's details from Plaid.
let createProcessorTokenAsync (client: PlaidClient) (accessToken: string) (accountId: string) (processor: string) ct =
    let body =
        {| access_token = accessToken
           account_id = accountId
           processor = processor |}

    Api.postAsync client "/processor/token/create" body (Json.requiredString "processor_token") ct

/// A one-time Stripe bank account token (btok_...) for the account.
let createStripeBankAccountTokenAsync (client: PlaidClient) (accessToken: string) (accountId: string) ct =
    let body =
        {| access_token = accessToken
           account_id = accountId |}

    Api.postAsync
        client
        "/processor/stripe/bank_account_token/create"
        body
        (Json.requiredString "stripe_bank_account_token")
        ct
