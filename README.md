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
- Accounts and balances, item removal, on-demand refresh
- Webhook verification (the ES256 `Plaid-Verification` JWT) and parsing into a typed event
- A replaceable storage layer (`IPlaidStore`), plus CSV export

Every call returns `Task<Result<_, PlaidFailure>>`. Plaid's error object comes back as `ApiError`, and cancelling
your token throws `OperationCanceledException` as usual.

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

// 3. Webhook endpoint: verify, parse, react. One KeyCache per process (a static in a Lambda).
match! Webhook.receiveCachedAsync keyCache client rawBody request.Headers[Webhook.VerificationHeader] DateTimeOffset.UtcNow ct with
| Ok(Webhook.SyncUpdatesAvailable(itemId, _, _)) ->
    let! _ = Sync.syncItemAsync client store itemId ct
    ()
| Ok(Webhook.PendingExpiration(itemId, _))         // UK/EU
| Ok(Webhook.PendingDisconnect(itemId, _, _))      // US/CA
| Ok(Webhook.ItemError(itemId, _)) -> (* ask the user to reconnect: Api.createUpdateLinkTokenAsync *) ()
| Ok _ -> ()
| Error _ -> (* reply 400 and ignore *) ()
```

Pass the webhook body exactly as received, before any JSON round trip: the signature covers its SHA-256.

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

## Other Plaid products

Typed support covers transactions. For other products (auth, assets, income, processor tokens, ...), make the
Link token with `Api.createLinkTokenForProductsAsync` and call their endpoints with `Api.postAsync` (your parser
over a `JsonElement`) or `Api.postRawAsync` (the reply as JSON text). Both add the credentials and `Plaid-Version`
headers and return Plaid errors as `ApiError`, like every other call.

```fsharp
// Link for assets and bank income: products plus any extra /link/token/create fields.
// A field the config also sets (country_codes, webhook, ...) takes the value given here.
let! token =
    Api.createLinkTokenForProductsAsync client userId [ "assets"; "income_verification" ]
        [ "user_token", box userToken
          "income_verification", box {| income_source_types = [| "bank" |]; bank_income = {| days_requested = 180 |} |} ]
        ct

// Any endpoint: no client_id or secret in the body.
let! report = Api.postRawAsync client "/asset_report/get" {| asset_report_token = reportToken |} ct
```

Webhooks of other products verify the same way (`Webhook.verifyAsync`). `Webhook.parse` returns them as `Other`
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
