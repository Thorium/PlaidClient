/// The HTTP layer (postAsync and friends), Link tokens, items, accounts, institutions, transactions sync,
/// webhook keys and sandbox helpers. Product-specific calls live in their own modules (Payments, Identity,
/// Auth, Assets, Income, Users). Every call returns Plaid's error object as ApiError instead of throwing;
/// cancellation of the caller's token still throws OperationCanceledException.
module Plaid.Api

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Plaid API version this client parses.
[<Literal>]
let ApiVersion = "2020-09-14"

let private failureOf status (body: string) =
    try
        use doc = JsonDocument.Parse body
        ApiError(Parse.apiError status doc.RootElement)
    with
    | :? JsonException
    | Json.InvalidJson _ -> HttpError(status, body)

/// Sends one POST and hands a success response to read; failures become PlaidFailure.
let private send
    (client: PlaidClient)
    (path: string)
    (body: obj)
    (read: HttpResponseMessage -> Task<Result<'T, PlaidFailure>>)
    (ct: CancellationToken)
    : Task<Result<'T, PlaidFailure>> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, client.Config.Environment.BaseUrl + path)
        request.Headers.Add("PLAID-CLIENT-ID", client.Config.ClientId)
        request.Headers.Add("PLAID-SECRET", client.Config.Secret)
        request.Headers.Add("Plaid-Version", ApiVersion)
        request.Content <- new StringContent(Request.serialize body, Encoding.UTF8, "application/json")

        try
            use! response = client.Http.SendAsync(request, ct)

            if response.IsSuccessStatusCode then
                return! read response
            else
                let! text = response.Content.ReadAsStringAsync()
                return Error(failureOf (int response.StatusCode) text)
        with
        | :? HttpRequestException as e -> return Error(TransportError e)
        | :? OperationCanceledException as e when not ct.IsCancellationRequested -> return Error(TransportError e)
    }

/// POSTs body to any Plaid endpoint and parses a success reply with parse. body is serialized with
/// System.Text.Json; ValueNone fields and empty F# lists are left out. Credentials and Plaid-Version go in
/// headers, so body needs neither. Use it for endpoints this library has no function for.
let postAsync
    (client: PlaidClient)
    (path: string)
    (body: obj)
    (parse: JsonElement -> 'T)
    (ct: CancellationToken)
    : Task<Result<'T, PlaidFailure>> =
    let read (response: HttpResponseMessage) =
        task {
            let! text = response.Content.ReadAsStringAsync()

            try
                use doc = JsonDocument.Parse text
                return Ok(parse doc.RootElement)
            with
            | :? JsonException as e -> return Error(InvalidResponse e.Message)
            | Json.InvalidJson message -> return Error(InvalidResponse message)
        }

    send client path body read ct

/// Same as postAsync for endpoints that answer with a file, e.g. a PDF report.
let postForBytesAsync (client: PlaidClient) (path: string) (body: obj) ct =
    let read (response: HttpResponseMessage) =
        task {
            let! bytes = response.Content.ReadAsByteArrayAsync()
            return Ok bytes
        }

    send client path body read ct

/// Same as postAsync, returning the success body as JSON text for the caller's own parser.
let postRawAsync (client: PlaidClient) (path: string) (body: obj) ct =
    postAsync client path body _.GetRawText() ct

/// The /link/token/create fields that come from the config. A dictionary would send a ValueNone as null,
/// so unset optional fields are not added at all.
let private linkFields (config: PlaidConfig) (clientUserId: string) : (string * obj) list =
    [ "client_name", box config.ClientName
      "language", box config.Language
      "country_codes", box config.CountryCodes
      "user", box {| client_user_id = clientUserId |}
      match config.RedirectUri with
      | ValueSome uri -> "redirect_uri", box uri
      | ValueNone -> ()
      match config.WebhookUrl with
      | ValueSome url -> "webhook", box url
      | ValueNone -> () ]

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
    createLinkToken client (linkFields client.Config clientUserId @ ("products", box products) :: fields) ct

/// Link token for connecting a new item with the transactions product.
let createLinkTokenAsync (client: PlaidClient) (clientUserId: string) ct =
    createLinkTokenForProductsAsync
        client
        clientUserId
        [ "transactions" ]
        [ "transactions", box {| days_requested = client.Config.TransactionsDaysRequested |} ]
        ct

/// Link token for update mode with extra /link/token/create fields, e.g. "android_package_name" for an
/// Android app. fields override the config's when they repeat one.
let createUpdateLinkTokenWithFieldsAsync
    (client: PlaidClient)
    (clientUserId: string)
    (accessToken: string)
    (fields: (string * obj) list)
    ct
    =
    createLinkToken client (linkFields client.Config clientUserId @ ("access_token", box accessToken) :: fields) ct

/// Link token for update mode: renews consent (PENDING_EXPIRATION) or repairs a broken login (ITEM_LOGIN_REQUIRED).
let createUpdateLinkTokenAsync (client: PlaidClient) (clientUserId: string) (accessToken: string) ct =
    createUpdateLinkTokenWithFieldsAsync client clientUserId accessToken [] ct

/// Exchanges the public token Link hands the browser for a permanent access token.
let exchangePublicTokenAsync (client: PlaidClient) (publicToken: string) ct =
    postAsync client "/item/public_token/exchange" {| public_token = publicToken |} Parse.itemAccess ct

/// Accounts and item status (institution, consent expiry, error) for an access token.
let getAccountsAsync (client: PlaidClient) (accessToken: string) ct =
    postAsync client "/accounts/get" {| access_token = accessToken |} Parse.accounts ct

/// Real-time balances (the Balance product): Plaid asks the bank instead of returning cached figures,
/// so it is slower than getAccountsAsync. accountIds limits it to those accounts ([] for all).
/// minLastUpdated is the oldest balance accepted where the bank cannot give a live one; Plaid needs it only
/// for some US credit accounts (Capital One) and ignores it elsewhere.
let getBalancesAsync
    (client: PlaidClient)
    (accessToken: string)
    (accountIds: string list)
    (minLastUpdated: DateTimeOffset voption)
    ct
    =
    let options =
        match accountIds, minLastUpdated with
        | [], ValueNone -> ValueNone
        | _ ->
            ValueSome
                {| account_ids = accountIds
                   min_last_updated_datetime = minLastUpdated |> ValueOption.map Request.timestamp |}

    postAsync client "/accounts/balance/get" {| access_token = accessToken; options = options |} Parse.accounts ct

/// Item status: products, consent, webhook, error, and when Plaid last updated it.
let getItemAsync (client: PlaidClient) (accessToken: string) ct =
    postAsync client "/item/get" {| access_token = accessToken |} Parse.itemDetails ct

/// Points the item's webhooks at webhookUrl, or turns them off with ValueNone. Plaid confirms with a
/// WEBHOOK_UPDATE_ACKNOWLEDGED webhook to the new URL.
let updateItemWebhookAsync (client: PlaidClient) (accessToken: string) (webhookUrl: string voption) ct =
    // Plaid removes the webhook on an explicit null. A record field would leave ValueNone out, a
    // dictionary entry writes it as null.
    let body = Map [ "access_token", box accessToken; "webhook", box webhookUrl ]
    postAsync client "/item/webhook/update" body Parse.item ct

/// One institution with its name, logo, brand colour and URL, searched in the config's countries.
let getInstitutionAsync (client: PlaidClient) (institutionId: string) ct =
    let body =
        {| institution_id = institutionId
           country_codes = client.Config.CountryCodes
           options = {| include_optional_metadata = true |} |}

    postAsync client "/institutions/get_by_id" body Parse.institutionById ct

/// Institutions whose name matches query in the config's countries (at most 10), optionally only those
/// supporting all of products ([] for any).
let searchInstitutionsAsync (client: PlaidClient) (query: string) (products: string list) ct =
    let body =
        {| query = query
           country_codes = client.Config.CountryCodes
           products = products
           options = {| include_optional_metadata = true |} |}

    postAsync client "/institutions/search" body Parse.institutions ct

/// Plaid's largest /transactions/sync page.
[<Literal>]
let MaxSyncPageSize = 500

/// The /transactions/sync options object for the config's SyncSettings; left out when nothing differs from
/// Plaid's defaults.
let private syncOptions (settings: SyncSettings) =
    match settings.IncludeOriginalDescription, settings.PersonalFinanceCategoryVersion with
    | false, ValueNone -> ValueNone
    | includeOriginal, version ->
        ValueSome
            {| include_original_description = Request.flag includeOriginal
               personal_finance_category_version = version |}

/// One /transactions/sync page after cursor; ValueNone or "" starts from the beginning of the item's history.
/// Until the first data arrives Plaid answers NOT_READY with no transactions and an empty next_cursor.
/// The options come from client.Config.Sync.
let syncTransactionsPageAsync (client: PlaidClient) (accessToken: string) (cursor: string voption) (count: int) ct =
    let body =
        {| access_token = accessToken
           cursor = cursor |> ValueOption.filter (String.IsNullOrEmpty >> not)
           count = count
           options = syncOptions client.Config.Sync |}

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
           initial_products = [ "transactions" ]
           options = {| webhook = client.Config.WebhookUrl |} |}

    postAsync client "/sandbox/public_token/create" body (Json.requiredString "public_token") ct

/// Sandbox only: makes Plaid send a webhook for the item, e.g. ("TRANSACTIONS", "SYNC_UPDATES_AVAILABLE")
/// or ("ITEM", "ERROR"). True when Plaid fired it.
let sandboxFireWebhookAsync (client: PlaidClient) (accessToken: string) (webhookType: string) (webhookCode: string) ct =
    let body =
        {| access_token = accessToken
           webhook_type = webhookType
           webhook_code = webhookCode |}

    postAsync client "/sandbox/item/fire_webhook" body (Json.required "webhook_fired" Json.tryBool) ct

/// Sandbox only: puts the item into ITEM_LOGIN_REQUIRED, to test update mode. True when it succeeded.
let sandboxResetLoginAsync (client: PlaidClient) (accessToken: string) ct =
    postAsync client "/sandbox/item/reset_login" {| access_token = accessToken |} (Json.required "reset_login" Json.tryBool) ct
