# PlaidClient

A standalone F# client for [Plaid](https://plaid.com) open banking, aimed at UK bank-transaction data. It has no
dependencies beyond FSharp.Core and System.Text.Json, and targets `netstandard2.0`,`net8.0` and `net10.0`, so .NET Framework
4.7.2+ and modern .NET applications can both use it. A .NET Framework project needs a reference to
`System.Net.Http` (`<Reference Include="System.Net.Http" />`), because `PlaidClient` holds an `HttpClient`.

What it covers:

- Link tokens for new items (`transactions` product) and for update mode (consent renewal, broken logins)
- Exchanging the Link public token and storing the item
- Incremental transaction sync (`/transactions/sync`), including the restart Plaid requires after
  `TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION`
- Accounts with cached or real-time balances, item status, webhook URL changes, item removal, on-demand refresh
- Institution lookup and search (name, logo, brand colour, OAuth)
- Webhook verification (the ES256 `Plaid-Verification` JWT) and parsing into a typed event
- A replaceable storage layer (`IPlaidStore`), plus CSV export
- Sandbox helpers: public tokens without Link, firing webhooks, forcing `ITEM_LOGIN_REQUIRED`
- UK/EU payments (`Payments`): recipients, payments and standing orders, Variable Recurring Payments consents
- Identity checks (`Identity`): account holders, Identity Match scores, Identity Verification (KYC)
- US lending (`Auth`, `Assets`, `Income`, `Users`): account and routing numbers, processor tokens (e.g. Checkbook),
  asset reports, bank and payroll income

Every call returns `Task<Result<_, PlaidFailure>>`. Plaid's error object comes back as `ApiError`, and cancelling
your token throws `OperationCanceledException` as usual.

## Handling failures

`PlaidFailure.action` (or `PlaidApiError.action` for the error inside an `ItemError` webhook) says what to do,
following Plaid's error reference:

| `FailureAction` | Typical cause | What to do |
| --- | --- | --- |
| `RetryLater` | Rate limits, `API_ERROR`, bank outage, network, HTTP 5xx | Retry the same call with backoff |
| `WaitForData` | `PRODUCT_NOT_READY` | Wait for the product's webhook, or retry later |
| `UpdateMode` | `ITEM_LOGIN_REQUIRED`, wrong credentials or MFA, consent not granted | Send the user through Link update mode |
| `UserActionAtInstitution` | `ITEM_LOCKED`, `PASSWORD_RESET_REQUIRED` | Ask the user to sort it out at the bank, then update mode |
| `ItemGone` | `ITEM_NOT_FOUND` | Delete the item locally |
| `Unsupported` | `PRODUCTS_NOT_SUPPORTED`, `NO_ACCOUNTS`, bank no longer supported | Offer another account or bank |
| `FixRequest` | Bad request or keys, exhausted credits | Fix the code or account; retrying fails the same way |

The library does not retry by itself: backoff timing and limits are the application's choice.

```fsharp
match! Sync.syncItemAsync client store itemId ct with
| Ok summary -> ...
| Error failure ->
    match PlaidFailure.action failure with
    | FailureAction.RetryLater | FailureAction.WaitForData -> scheduleRetry itemId
    | FailureAction.UpdateMode | FailureAction.UserActionAtInstitution -> askUserToReconnect itemId
    | FailureAction.ItemGone -> do! store.DeleteItemAsync(itemId, ct)
    | FailureAction.Unsupported | FailureAction.FixRequest -> logError failure
```

## Items, balances and institutions

- `Api.getBalancesAsync client accessToken accountIds minLastUpdated ct`: live balances from the bank (the Balance
  product), slower than the cached ones in `getAccountsAsync`. Pass `[]` for all accounts; `minLastUpdated` matters
  only for some US credit accounts.
- `Api.getItemAsync`: products (in use, billed, available, consented), consent expiry, webhook, error, and when
  transactions last updated.
- `Api.updateItemWebhookAsync client accessToken (ValueSome url) ct` moves an item's webhooks; `ValueNone` turns
  them off. Plaid confirms with `WebhookUpdateAcknowledged` at the new URL.
- `Api.getInstitutionAsync` / `searchInstitutionsAsync` look up banks in the config's countries, with name,
  logo (base64 PNG), brand colour, URL and whether they use OAuth.
- Sandbox: `Api.sandboxFireWebhookAsync client accessToken "TRANSACTIONS" "SYNC_UPDATES_AVAILABLE" ct` makes Plaid
  send a webhook, and `Api.sandboxResetLoginAsync` puts an item into `ITEM_LOGIN_REQUIRED` to test update mode.

## Flow

```fsharp
open Plaid

let client =
    { Config =
        { PlaidConfig.uk clientId secret PlaidEnvironment.Production "Your Company" with
            RedirectUri = ValueSome "https://app.example.co.uk/plaid/oauth"   // required for UK OAuth banks
            WebhookUrl = ValueSome "https://app.example.co.uk/plaid/webhook" }
      Http = httpClient }   // you own the HttpClient (IHttpClientFactory works)

// 1. Server: make a link token, hand it to Plaid Link in the browser.
let! token = Api.createLinkTokenAsync client userId ct

// 2. Server: Link's onSuccess gives the browser a public token. Linking stores the item, reads its
//    accounts and runs the first sync: Plaid sends SYNC_UPDATES_AVAILABLE only after one sync call.
//    On Error after the exchange the item is already stored; retry with Sync.syncItemAsync.
let! linked = Sync.linkItemAsync client store userId publicToken ct

// 3. Webhook endpoint: verify, queue the work, answer. See "Receiving webhooks".
```

## Receiving webhooks

Every webhook, whatever the product, carries a `Plaid-Verification` JWT. `Webhook.receiveCachedAsync` checks it
(ES256 signature with Plaid's key, at most 5 minutes old, SHA-256 of the body) and then parses the body into a
`WebhookEvent`. `Webhook.statusCodeFor` turns the result into the status to answer with.

```fsharp
// ASP.NET Core; one KeyCache per process (a static in a Lambda).
let keyCache = Webhook.KeyCache(TimeSpan.FromHours 1.0)

app.MapPost("/plaid/webhook", Func<HttpRequest, Task<IResult>>(fun request ->
    task {
        // The raw body, exactly as sent: not a model-bound object, not re-serialized JSON.
        use reader = new StreamReader(request.Body, Text.Encoding.UTF8)
        let! body = reader.ReadToEndAsync()
        let jwt = string request.Headers[Webhook.VerificationHeader]
        let! result = Webhook.receiveCachedAsync keyCache client body jwt DateTimeOffset.UtcNow request.HttpContext.RequestAborted

        match result with
        | Ok event -> do! queue.EnqueueAsync event        // do the work after answering
        | Error _ -> ()

        return Results.StatusCode(Webhook.statusCodeFor result)
    }))
```

What Plaid expects of the endpoint:

- **Answer within 10 seconds** with 2xx, or the delivery counts as failed. Queue the work (`Sync.syncItemAsync`,
  `Assets.getReportAsync`, ...) instead of doing it in the request.
- **Retries:** Plaid retries non-2xx answers for up to 24 hours, but most 4xx are final. `statusCodeFor` answers 400
  for a webhook that fails verification and 503 when the signing key could not be fetched, so a genuine webhook
  comes again.
- **Duplicates and out-of-order delivery happen.** Make handlers idempotent (syncs already are). Payment and consent
  events carry a `timestamp`; ignore one older than the last applied, or read the current state with
  `Payments.getPaymentAsync`.
- **The body must be byte-for-byte what Plaid sent:** the signature covers its SHA-256, and Plaid indents with two
  spaces. Read it as UTF-8 text before any JSON parsing.
- **Optional hardening:** a webhook naming an unknown key id costs one `/webhook_verification_key/get` call, so a
  flood of forged requests spends your Plaid rate limit. Rate-limit the endpoint, or allow only Plaid's published
  sender IPs (52.21.26.131, 52.21.47.157, 52.41.247.19, 52.88.82.239, which Plaid says may change).

`Webhook.KeyCache(refreshAfter)` keeps Plaid's signing keys by key id, so repeated webhooks cost no extra Plaid
call. A cached key is fetched again once it is older than `refreshAfter`. That is also how long a key Plaid has
rotated out keeps verifying new webhooks, because only a fresh fetch shows its `expired_at`. Failed fetches are not
cached. `receiveAsync` / `verifyAsync` fetch the key on every call instead.

`Transaction.Amount` uses Plaid's sign convention: positive means money left the account.

## Sync settings

`PlaidConfig.Sync` controls `/transactions/sync`:

| Field | Default | Effect |
| --- | --- | --- |
| `IncludeOriginalDescription` | `false` | Asks for the bank's raw description, read as `Transaction.OriginalDescription` |
| `PersonalFinanceCategoryVersion` | `ValueNone` | Category taxonomy, e.g. `ValueSome "v2"`; none means the account's default |
| `MaxRestarts` | `3` | Restarts after `TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION` before the sync returns that error |

```fsharp
let config =
    { PlaidConfig.uk clientId secret PlaidEnvironment.Production "Your Company" with
        Sync = { SyncSettings.defaults with IncludeOriginalDescription = true; PersonalFinanceCategoryVersion = ValueSome "v2" } }
```

A sync that gives up leaves the stored cursor and transactions as they were, so the next webhook or retry starts
over from the same point.

## Mobile apps

Plaid's React Native and Android SDKs need `android_package_name` on the Link token for Android. Pass it as an
extra field, in update mode too:

```fsharp
let android: (string * obj) list = [ "android_package_name", box "com.example.app" ]
let! token = Api.createLinkTokenForProductsAsync client userId [ "transactions" ] android ct
let! renew = Api.createUpdateLinkTokenWithFieldsAsync client userId accessToken android ct
```

Use a config with `RedirectUri = ValueNone` for those calls if your Plaid setup wants no redirect URI on Android.

## UK/EU payments with identity checks

Payment Initiation (`Payments`) moves money from the payer's bank, but does not itself check who owns the paying
account. Where regulation asks for the payer's identity, combine three steps:

1. **Who the customer is (KYC):** `Identity.createVerificationAsync` + `Identity.createVerificationLinkTokenAsync`
   run Plaid's Identity Verification template (document, selfie, data and watchlist checks). Link's `onSuccess`
   only means the flow finished: read the outcome with `Identity.getVerificationAsync`, or on the
   `IdentityVerificationUpdated` webhook.
2. **That the paying account is theirs:** link the payer's bank with `auth` and `identity`, then
   `Identity.matchAsync` scores the bank's account holder against the customer (`Identity.nameMatches
   Identity.RecommendedThreshold` for Plaid's 70+ rule).
3. **Pay only from that account:** `PayerAccount.ofAuth authNumbers accountId` gives the UK (BACS) or EU (IBAN)
   numbers to put in `PaymentRequest.PayerAccount`. Banks enforce it where they can.

```fsharp
let! user = Users.createAsync client customerId (ValueSome { UserIdentity.create "Ada" "Lovelace" with Emails = [ email ] }) ct
let! recipient = Payments.createRecipientAsync client "Your Company Ltd" (Payments.RecipientBacs yourBacs) ValueNone ct
let request =
    { Payments.PaymentRequest.create recipientId "Invoice 42" { Currency = "GBP"; Value = 125.50m } with
        UserId = ValueSome userId
        PayerAccount = Payments.PayerAccount.ofAuth auth.Numbers verifiedAccountId }
let! payment = Payments.createPaymentAsync client request ct      // reference checked locally: 1-18 letters, digits, spaces
let! token = Payments.createPaymentLinkTokenAsync client customerId (ValueSome userId) paymentId ct
// Then PaymentStatusUpdate webhooks: PaymentStatus.isSuccessful / isFailed
```

Standing orders use `PaymentRequest.Schedule`. Variable Recurring Payments (UK) use `Payments.createConsentAsync`
(with `ConsentPayer` naming the payer's own account), `createConsentLinkTokenAsync` and
`executeConsentPaymentAsync` (with an idempotency key). Refunds need Plaid virtual accounts and are not typed.

New Plaid integrations name the customer with a `user_id` from `Users.createAsync`; payments require it.

## US lending

The typed US flows match a lending app's usual needs:

| Need | Calls |
| --- | --- |
| Bank account and routing numbers | `Auth.getAsync` (also UK BACS, EU IBAN, Canadian EFT) |
| Hand the account to a payment partner | `Auth.createProcessorTokenAsync client accessToken accountId Auth.Processor.Checkbook`, `createStripeBankAccountTokenAsync` |
| The customer as a Plaid user | `Users.createAsync` (new `identity`), or `Users.createLegacyAsync` (`consumer_report_user_identity`, integrations before 2025-12-10); `CreatedUser.toUserRef` gives the `UserRef` (user_id or legacy user_token) later calls need |
| Assets for underwriting | `Assets.createReportAsync`, then on `AssetReportReady` `Assets.getReportAsync` / `getReportPdfAsync`; also `refreshReportAsync`, `filterReportAsync`, `removeReportAsync` |
| Bank income | `Income.createBankIncomeLinkTokenAsync`, then after Link `Income.getBankIncomeAsync` (no webhook) |
| Payroll income | `Income.createPayrollIncomeLinkTokenAsync` with `[ PayrollFlow.Digital ]` and/or `Document`, then on `IncomeVerification` `Income.getPayrollIncomeAsync` |

Account numbers, SSNs, user tokens and asset report tokens print masked (`ToString`, `%A`).

For new US income integrations Plaid recommends Consumer Report (Plaid Check, `/cra/...`), which is not typed here.

## Other Plaid products

For anything without a typed function (Transfer, Liabilities, Investments, Consumer Report, Monitor, payment
refunds, ...), make the Link token with `Api.createLinkTokenForProductsAsync` and call the endpoints with
`Api.postAsync` (your parser over a `JsonElement`), `Api.postRawAsync` (the reply as JSON text) or
`Api.postForBytesAsync` (files). They add the credentials and `Plaid-Version` headers and return Plaid errors as
`ApiError`, like every other call. Write the body as an F# record or anonymous record with Plaid's field names: a
`ValueNone` field or an empty list is left out of the request, so optional fields need no `null`.

```fsharp
// Products plus any extra /link/token/create fields; a field the config also sets takes the value given here.
let! token = Api.createLinkTokenForProductsAsync client userId [ "liabilities" ] [] ct
let! liabilities = Api.postRawAsync client "/liabilities/get" {| access_token = accessToken |} ct
```

Webhooks of untyped products verify the same way (`Webhook.verifyAsync`). `Webhook.parse` returns them as `Other`
with their type and code, so read the remaining fields from the verified body yourself.

## Storage

The library does not decide where data lives. `Sync` reads and writes through `IPlaidStore`:

| Member | Purpose |
| --- | --- |
| `SaveItemAsync` / `TryGetItemAsync` / `GetUserItemsAsync` / `DeleteItemAsync` | Linked items: access token, sync cursor, institution, consent expiry |
| `ApplySyncAsync` | One completed sync: upsert added and modified transactions, delete removed ones, then store the new cursor |
| `GetTransactionsAsync` | An item's current transactions |

Two implementations are included, **for development and tests only**:

- `JsonFilePlaidStore(folder)`: `items.json` plus `transactions/{itemId}.json`, holding Plaid's own transaction
  objects so no field is lost. Access tokens are written **in plain text**.
- `InMemoryPlaidStore()`

A real application implements `IPlaidStore` over its own database. That implementation should:

- encrypt `AccessToken` at rest, because it grants ongoing read access to the customer's bank data
- make `ApplySyncAsync` atomic: changes and cursor in one database transaction. A webhook and a manual sync of
  the same item can overlap, and a non-atomic apply can then leave older data under a newer cursor. Without
  transactions, write the changes before the cursor and run one sync per item at a time.
- keep `Transaction.Raw` if you want every field Plaid sends. `Transaction.ofRaw` rebuilds the record from it.

`Csv.ofTransactions` / `Csv.writeTransactions` export transactions as RFC 4180 CSV. Text taken from the bank that
starts with `= + - @` gets a leading apostrophe so spreadsheets do not run it as a formula.

## Regions

The API calls, sync and webhook verification are the same in every region. What differs:

| | UK / EU | US / CA |
| --- | --- | --- |
| Config | `PlaidConfig.uk`, or `PlaidConfig.create ... ["IE"; "FR"; ...]` (set `Language` for non-English Link) | `PlaidConfig.us`, or `create ... ["US"; "CA"]` |
| OAuth redirect | Needed for practically every bank: register it in the dashboard and set `RedirectUri` | Needed for some large banks (OAuth institutions); set it if you offer them |
| Losing access | Consent expires: `PENDING_EXPIRATION` webhook, date in `ConsentExpiration` | `PENDING_DISCONNECT` webhook with reason and `disconnect_time`; `ConsentExpiration` usually empty |
| Recovery | Link in update mode (`Api.createUpdateLinkTokenAsync`, or `createUpdateLinkTokenWithFieldsAsync` for extra fields), then `Sync.refreshItemInfoAsync` for the new expiry | Same |

Some fields exist only in some regions (for example `transaction_type` and counterparty details for European
banks). The typed `Transaction` keeps the common fields; everything else is in `Transaction.Raw`.

`TransactionsDaysRequested` defaults to 730, Plaid's maximum. The first sync returns what the bank provides.

## Build and test

```bash
dotnet build -c Release
```

```bash
dotnet test
```

The tests run against a fake Plaid HTTP handler. Webhook tests sign JWTs with a P-256 key generated during the test,
so no Plaid credentials are needed.

## Licence

[The Unlicense](LICENSE): public domain.

## Examples

`examples/sandbox.fsx` runs the full flow against the Plaid sandbox. It needs `PLAID_CLIENT_ID` and `PLAID_SECRET`,
and writes the store and a CSV under `examples/data/`.

`examples/webhook-check.fsx` checks webhook verification against real Plaid webhooks. It links a sandbox item
whose webhook URL is a new [webhook.site](https://webhook.site) address, and makes Plaid send
`SYNC_UPDATES_AVAILABLE`, `WEBHOOK_UPDATE_ACKNOWLEDGED` and an `ITEM_LOGIN_REQUIRED` error. It then reads each
webhook back and runs it through `Webhook.receiveCachedAsync`, and exits 0 when all of them verify. It needs the
same sandbox keys. Anyone with the webhook.site address can read what it received, so use sandbox keys only.
