namespace Plaid

open System
open System.Net.Http

module internal Mask =

    /// "****1234": enough to recognise an account number or IBAN in a log, not enough to use it.
    let number (value: string) =
        if value.Length <= 4 then "****" else "****" + value.Substring(value.Length - 4)

/// Plaid environment the client talks to.
[<RequireQualifiedAccess>]
type PlaidEnvironment =
    | Sandbox
    | Production

    member this.BaseUrl =
        match this with
        | Sandbox -> "https://sandbox.plaid.com"
        | Production -> "https://production.plaid.com"

/// How /transactions/sync is called.
type SyncSettings =
    { /// Asks for original_description, the bank's raw text for each transaction.
      IncludeOriginalDescription: bool
      /// Category taxonomy, e.g. "v2". ValueNone leaves Plaid's default for the account.
      PersonalFinanceCategoryVersion: string voption
      /// Restarts after TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION before the sync returns that error.
      /// A failed sync leaves the stored cursor as it was, so the next sync starts over safely.
      MaxRestarts: int }

module SyncSettings =

    /// Plaid's defaults: no original description, the account's default taxonomy, three restarts.
    let defaults =
        { IncludeOriginalDescription = false
          PersonalFinanceCategoryVersion = ValueNone
          MaxRestarts = 3 }

/// Credentials and Link settings. Printing it (ToString, %A) hides the secret.
[<StructuredFormatDisplay("{Display}")>]
type PlaidConfig =
    { ClientId: string
      Secret: string
      Environment: PlaidEnvironment
      /// Name shown to the end user in Link.
      ClientName: string
      /// ISO 3166-1 alpha-2 codes of the institutions Link offers, e.g. ["GB"].
      CountryCodes: string list
      Language: string
      /// Registered OAuth redirect URI. UK banks connect through OAuth, so Link needs it there.
      RedirectUri: string voption
      /// Receives item and transactions webhooks.
      WebhookUrl: string voption
      /// History requested when an item is linked, 1..730 days.
      TransactionsDaysRequested: int
      Sync: SyncSettings }

    member this.Display =
        $"PlaidConfig {{ ClientId = %s{this.ClientId}; Secret = ***; Environment = {this.Environment}; CountryCodes = %A{this.CountryCodes} }}"

    override this.ToString() = this.Display

module PlaidConfig =

    /// English Link for the given countries, the maximum 730 days of history, no redirect URI or webhook,
    /// SyncSettings.defaults.
    let create clientId secret environment clientName (countryCodes: string list) =
        { ClientId = clientId
          Secret = secret
          Environment = environment
          ClientName = clientName
          CountryCodes = countryCodes
          Language = "en"
          RedirectUri = ValueNone
          WebhookUrl = ValueNone
          TransactionsDaysRequested = 730
          Sync = SyncSettings.defaults }

    /// UK institutions. They connect through OAuth, so set RedirectUri as well.
    let uk clientId secret environment clientName =
        create clientId secret environment clientName [ "GB" ]

    let us clientId secret environment clientName =
        create clientId secret environment clientName [ "US" ]

/// Configuration plus the HttpClient used for every call. The caller owns the HttpClient.
type PlaidClient = { Config: PlaidConfig; Http: HttpClient }

/// Error object Plaid returns with a non-success status.
type PlaidApiError =
    { HttpStatus: int
      ErrorType: string
      ErrorCode: string
      ErrorMessage: string
      /// Safe to show to the end user when present.
      DisplayMessage: string voption
      RequestId: string voption }

type PlaidFailure =
    | ApiError of PlaidApiError
    | TransportError of exn
    /// A non-success reply without Plaid's error object, e.g. an HTML page from a proxy.
    | HttpError of status: int * body: string
    /// A success reply this client could not read.
    | InvalidResponse of message: string
    | UnknownItem of itemId: string
    | WebhookRejected of reason: string
    /// Webhook.KeyCache did not ask Plaid for this unknown key id: another one was fetched moments ago.
    | KeyFetchLimited of keyId: string
    /// A value the client refused before calling Plaid, e.g. a payment reference Plaid would reject.
    | InvalidArgument of message: string

/// What the application should do about a failure.
[<RequireQualifiedAccess>]
type FailureAction =
    /// Temporary (rate limit, Plaid or bank outage, network): retry the same call with backoff.
    | RetryLater
    /// The data is still being prepared (PRODUCT_NOT_READY): wait for the product's webhook, or retry later.
    | WaitForData
    /// The end user must go through Link update mode (ITEM_LOGIN_REQUIRED, credentials, MFA, consent).
    | UpdateMode
    /// The end user must sort something out at the bank first (locked account, password reset), then use update mode.
    | UserActionAtInstitution
    /// The item no longer exists at Plaid: delete it locally.
    | ItemGone
    /// This item, bank or product cannot be served: offer another account or bank.
    | Unsupported
    /// A request, configuration or account problem: the same call fails the same way until it is fixed.
    | FixRequest

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PlaidApiError =

    /// Maps Plaid's error_type and error_code to an action, following Plaid's error reference.
    let action (e: PlaidApiError) : FailureAction =
        match e.ErrorType, e.ErrorCode with
        | "RATE_LIMIT_EXCEEDED", ("CREDITS_EXHAUSTED" | "TRIAL_CONNECTION_LIMIT") -> FailureAction.FixRequest
        | "RATE_LIMIT_EXCEEDED", _
        | "API_ERROR", _
        | "INSTITUTION_ERROR", ("INSTITUTION_DOWN" | "INSTITUTION_NOT_RESPONDING" | "INSTITUTION_NOT_AVAILABLE")
        | _, ("TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION" | "ASSET_REPORT_GENERATION_FAILED") -> FailureAction.RetryLater
        | "INSTITUTION_ERROR", _ -> FailureAction.Unsupported
        | _, "PRODUCT_NOT_READY" -> FailureAction.WaitForData
        | _,
          ("ITEM_LOGIN_REQUIRED"
          | "INSUFFICIENT_CREDENTIALS"
          | "INVALID_CREDENTIALS"
          | "INVALID_MFA"
          | "INVALID_OTP"
          | "INVALID_PHONE_NUMBER"
          | "INVALID_UPDATED_USERNAME"
          | "USER_INPUT_TIMEOUT"
          | "ACCESS_NOT_GRANTED") -> FailureAction.UpdateMode
        | _, ("ITEM_LOCKED" | "PASSWORD_RESET_REQUIRED" | "USER_SETUP_REQUIRED") ->
            FailureAction.UserActionAtInstitution
        | _, ("ITEM_NOT_FOUND" | "ITEM_CONCURRENTLY_DELETED") -> FailureAction.ItemGone
        | _,
          ("ITEM_NOT_SUPPORTED"
          | "MFA_NOT_SUPPORTED"
          | "PRODUCTS_NOT_SUPPORTED"
          | "NO_ACCOUNTS"
          | "NO_AUTH_ACCOUNTS"
          | "NO_INVESTMENT_ACCOUNTS"
          | "NO_LIABILITY_ACCOUNTS") -> FailureAction.Unsupported
        // Plaid: 5xx replies are on Plaid's side.
        | _ when e.HttpStatus >= 500 -> FailureAction.RetryLater
        | _ -> FailureAction.FixRequest

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PlaidFailure =

    let action (failure: PlaidFailure) : FailureAction =
        match failure with
        | ApiError e -> PlaidApiError.action e
        | TransportError _ -> FailureAction.RetryLater
        | KeyFetchLimited _ -> FailureAction.RetryLater
        | HttpError(status, _) when status >= 500 || status = 429 -> FailureAction.RetryLater
        | HttpError _
        | InvalidResponse _
        | UnknownItem _
        | WebhookRejected _
        | InvalidArgument _ -> FailureAction.FixRequest

type LinkToken =
    { LinkToken: string
      Expiration: DateTimeOffset
      RequestId: string }

/// Result of exchanging a Link public token. The access token is a long-lived secret; printing hides it.
[<StructuredFormatDisplay("{Display}")>]
type ItemAccess =
    { ItemId: string
      AccessToken: string }

    member this.Display = $"ItemAccess {{ ItemId = {this.ItemId}; AccessToken = *** }}"
    override this.ToString() = this.Display

type Balances =
    { Available: decimal voption
      Current: decimal voption
      Limit: decimal voption
      IsoCurrencyCode: string voption
      UnofficialCurrencyCode: string voption
      /// When the bank last updated the balance; only some institutions report it.
      LastUpdated: DateTimeOffset voption }

type Account =
    { AccountId: string
      Name: string
      OfficialName: string voption
      Mask: string voption
      Type: string
      Subtype: string voption
      Balances: Balances }

type ItemInfo =
    { ItemId: string
      InstitutionId: string voption
      InstitutionName: string voption
      /// When the end user's consent ends; UK/EU items must be renewed through update mode before it.
      ConsentExpirationTime: DateTimeOffset voption
      Error: PlaidApiError voption
      Webhook: string voption
      /// Products in use, products being billed, products the item could still add, products consented to.
      Products: string list
      BilledProducts: string list
      AvailableProducts: string list
      ConsentedProducts: string list
      /// "background" for items Plaid updates by itself, "user_present_required" for ones that need the user.
      UpdateType: string voption }

type AccountsResult = { Item: ItemInfo; Accounts: Account list }

/// An email address or phone number as the bank reports it.
type ContactDetail =
    { Data: string
      Primary: bool
      /// e.g. "primary", "secondary", "mobile", "home".
      Type: string voption }

type PostalAddress =
    { Street: string voption
      City: string voption
      Region: string voption
      PostalCode: string voption
      /// ISO 3166-1 alpha-2.
      Country: string voption }

module PostalAddress =

    let empty =
        { Street = ValueNone
          City = ValueNone
          Region = ValueNone
          PostalCode = ValueNone
          Country = ValueNone }

type AddressDetail = { Data: PostalAddress; Primary: bool }

/// An account holder as the bank reports it. One owner can stand for all holders of a joint account;
/// only Names is guaranteed, the other lists are empty when the bank does not share them.
type Owner =
    { Names: string list
      Emails: ContactDetail list
      PhoneNumbers: ContactDetail list
      Addresses: AddressDetail list }

/// When Plaid last updated an item, and its last webhook.
type ItemStatus =
    { TransactionsLastSuccessfulUpdate: DateTimeOffset voption
      TransactionsLastFailedUpdate: DateTimeOffset voption
      LastWebhookSentAt: DateTimeOffset voption
      LastWebhookCode: string voption }

type ItemDetails = { Item: ItemInfo; Status: ItemStatus }

type Institution =
    { InstitutionId: string
      Name: string
      Products: string list
      CountryCodes: string list
      Url: string voption
      /// Brand colour as hex, e.g. "#1f2a44".
      PrimaryColor: string voption
      /// Base64 152x152 PNG; not every institution has one.
      Logo: string voption
      /// Connects through OAuth, so Link needs PlaidConfig.RedirectUri.
      Oauth: bool }

type PersonalFinanceCategory =
    { Primary: string
      Detailed: string
      ConfidenceLevel: string voption }

type Transaction =
    { TransactionId: string
      AccountId: string
      /// Positive when money leaves the account, negative when it arrives.
      Amount: decimal
      IsoCurrencyCode: string voption
      UnofficialCurrencyCode: string voption
      /// Posting date, or the transaction date while pending.
      Date: DateTime
      AuthorizedDate: DateTime voption
      Name: string
      /// The bank's raw description; present when SyncSettings.IncludeOriginalDescription is set.
      OriginalDescription: string voption
      MerchantName: string voption
      Pending: bool
      PendingTransactionId: string voption
      PaymentChannel: string
      Category: PersonalFinanceCategory voption
      /// The transaction object exactly as Plaid sent it, including fields not modelled here.
      Raw: string }

type RemovedTransaction =
    { TransactionId: string
      AccountId: string voption }

[<RequireQualifiedAccess>]
type TransactionsUpdateStatus =
    | Unknown
    | NotReady
    | InitialUpdateComplete
    | HistoricalUpdateComplete

/// One /transactions/sync page.
type SyncPage =
    { Added: Transaction list
      Modified: Transaction list
      Removed: RemovedTransaction list
      NextCursor: string
      HasMore: bool
      UpdateStatus: TransactionsUpdateStatus
      Accounts: Account list }

/// Changes of one complete sync, in the order Plaid sent them.
type TransactionChanges =
    { Added: Transaction list
      Modified: Transaction list
      Removed: RemovedTransaction list }

module TransactionChanges =

    let empty: TransactionChanges = { Added = []; Modified = []; Removed = [] }

    let ofPages (pages: SyncPage list) : TransactionChanges =
        { Added = pages |> List.collect _.Added
          Modified = pages |> List.collect _.Modified
          Removed = pages |> List.collect _.Removed }

    /// Upserts added and modified transactions, then drops removed ones.
    let apply (changes: TransactionChanges) (existing: Map<string, Transaction>) =
        let upserted =
            changes.Added @ changes.Modified
            |> List.fold (fun acc (t: Transaction) -> acc |> Map.add t.TransactionId t) existing

        changes.Removed
        |> List.fold (fun acc (r: RemovedTransaction) -> acc |> Map.remove r.TransactionId) upserted

type WebhookVerificationKey =
    { KeyId: string
      Algorithm: string
      Curve: string
      /// Base64url EC public key coordinates.
      X: string
      Y: string
      ExpiredAt: DateTimeOffset voption }
