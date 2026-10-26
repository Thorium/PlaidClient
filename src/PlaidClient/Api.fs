/// One function per Plaid endpoint. Every call returns Plaid's error object as ApiError instead of throwing;
/// cancellation of the caller's token still throws OperationCanceledException.
module Plaid.Api

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks

let private serializerOptions =
    JsonSerializerOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)

/// Plaid API version this client parses.
[<Literal>]
let ApiVersion = "2020-09-14"

let private failureOf status (body: string) =
    try
        use doc = JsonDocument.Parse body
        ApiError(Parse.apiError status doc.RootElement)
    with
    | :? JsonException
    | Json.InvalidJson _ -> InvalidResponse $"HTTP {status}: {body}"

/// POSTs body (serialized with System.Text.Json, null fields left out) to any Plaid endpoint and parses
/// a success reply with parse. Credentials and Plaid-Version go in headers, so body needs neither.
/// Use it for endpoints this module has no function for (assets, income, processor tokens, ...).
let postAsync
    (client: PlaidClient)
    (path: string)
    (body: obj)
    (parse: JsonElement -> 'T)
    (ct: CancellationToken)
    : Task<Result<'T, PlaidFailure>> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, client.Config.Environment.BaseUrl + path)
        request.Headers.Add("PLAID-CLIENT-ID", client.Config.ClientId)
        request.Headers.Add("PLAID-SECRET", client.Config.Secret)
        request.Headers.Add("Plaid-Version", ApiVersion)
        request.Content <- new StringContent(JsonSerializer.Serialize(body, serializerOptions), Encoding.UTF8, "application/json")

        try
            use! response = client.Http.SendAsync(request, ct)
            let! text = response.Content.ReadAsStringAsync()

            if response.IsSuccessStatusCode then
                try
                    use doc = JsonDocument.Parse text
                    return Ok(parse doc.RootElement)
                with
                | :? JsonException as e -> return Error(InvalidResponse e.Message)
                | Json.InvalidJson message -> return Error(InvalidResponse message)
            else
                return Error(failureOf (int response.StatusCode) text)
        with
        | :? HttpRequestException as e -> return Error(TransportError e)
        | :? OperationCanceledException as e when not ct.IsCancellationRequested -> return Error(TransportError e)
    }

/// Same as postAsync, returning the success body as JSON text for the caller's own parser.
let postRawAsync (client: PlaidClient) (path: string) (body: obj) ct =
    postAsync client path body _.GetRawText() ct

/// The /link/token/create fields that come from the config.
let private linkFields (config: PlaidConfig) (clientUserId: string) : (string * obj) list =
    let optional =
        [ "redirect_uri", config.RedirectUri; "webhook", config.WebhookUrl ]
        |> List.choose (fun (name, value) ->
            match value with
            | ValueSome v -> Some(name, box v)
            | ValueNone -> None)

    [ "client_name", box config.ClientName
      "language", box config.Language
      "country_codes", box (Array.ofList config.CountryCodes)
      "user", box {| client_user_id = clientUserId |} ]
    @ optional

let private createLinkToken client (fields: (string * obj) list) ct =
    // Map.ofList keeps the last value per name, so later fields override the config's.
    postAsync client "/link/token/create" (Map.ofList fields) Parse.linkToken ct

/// Link token for any products, e.g. ["auth"] or ["assets"; "income_verification"]. fields adds
/// /link/token/create request fields such as "user_token" or "income_verification", and overrides
/// the config's ("country_codes", "webhook", ...) when it repeats one.
/// clientUserId is the application's stable, non-identifying id for the end user.
let createLinkTokenForProductsAsync
    (client: PlaidClient)
    (clientUserId: string)
    (products: string list)
    (fields: (string * obj) list)
    ct
    =
    createLinkToken client (linkFields client.Config clientUserId @ ("products", box (Array.ofList products)) :: fields) ct

/// Link token for connecting a new item with the transactions product.
let createLinkTokenAsync (client: PlaidClient) (clientUserId: string) ct =
    createLinkTokenForProductsAsync
        client
        clientUserId
        [ "transactions" ]
        [ "transactions", box {| days_requested = client.Config.TransactionsDaysRequested |} ]
        ct

/// Link token for update mode: renews consent (PENDING_EXPIRATION) or repairs a broken login (ITEM_LOGIN_REQUIRED).
let createUpdateLinkTokenAsync (client: PlaidClient) (clientUserId: string) (accessToken: string) ct =
    createLinkToken client (linkFields client.Config clientUserId @ [ "access_token", box accessToken ]) ct

/// Exchanges the public token Link hands the browser for a permanent access token.
let exchangePublicTokenAsync (client: PlaidClient) (publicToken: string) ct =
    postAsync client "/item/public_token/exchange" {| public_token = publicToken |} Parse.itemAccess ct

/// Accounts and item status (institution, consent expiry, error) for an access token.
let getAccountsAsync (client: PlaidClient) (accessToken: string) ct =
    postAsync client "/accounts/get" {| access_token = accessToken |} Parse.accounts ct

/// Plaid's largest /transactions/sync page.
[<Literal>]
let MaxSyncPageSize = 500

/// One /transactions/sync page after cursor; ValueNone or "" starts from the beginning of the item's history.
/// Until the first data arrives Plaid answers NOT_READY with no transactions and an empty next_cursor.
let syncTransactionsPageAsync (client: PlaidClient) (accessToken: string) (cursor: string voption) (count: int) ct =
    let body =
        {| access_token = accessToken
           cursor = cursor |> ValueOption.filter (String.IsNullOrEmpty >> not) |> ValueOption.toObj
           count = count |}

    postAsync client "/transactions/sync" body Parse.syncPage ct

/// Asks Plaid to fetch new transactions now; results arrive through SYNC_UPDATES_AVAILABLE. A billed add-on.
let refreshTransactionsAsync (client: PlaidClient) (accessToken: string) ct =
    postAsync client "/transactions/refresh" {| access_token = accessToken |} ignore ct

/// Invalidates the access token and ends billing for the item.
let removeItemAsync (client: PlaidClient) (accessToken: string) ct =
    postAsync client "/item/remove" {| access_token = accessToken |} ignore ct

let getWebhookVerificationKeyAsync (client: PlaidClient) (keyId: string) ct =
    postAsync client "/webhook_verification_key/get" {| key_id = keyId |} Parse.webhookKey ct

/// Sandbox only: a public token for an institution without going through Link.
let sandboxCreatePublicTokenAsync (client: PlaidClient) (institutionId: string) ct =
    let body =
        {| institution_id = institutionId
           initial_products = [| "transactions" |]
           options = {| webhook = client.Config.WebhookUrl |> ValueOption.toObj |} |}

    postAsync client "/sandbox/public_token/create" body (Json.required "public_token" Json.tryString) ct
