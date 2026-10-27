// Checks webhook verification against real Plaid webhooks: links a Plaid sandbox item whose webhooks go to
// a webhook.site URL, makes Plaid send several, reads them back from webhook.site and runs each through
// Webhook.receiveCachedAsync exactly as a webhook endpoint would.
//
//   dotnet build -c Release
//   $env:PLAID_CLIENT_ID = "..."; $env:PLAID_SECRET = "<sandbox secret>"
//   dotnet fsi examples/webhook-check.fsx
//
// WEBHOOK_SITE_TOKEN reuses a webhook.site token; otherwise a new anonymous one is created. Anyone who knows
// a webhook.site URL can read what it received, so use sandbox credentials only.
// PLAID_INSTITUTION picks the sandbox institution (default ins_109508, First Platypus Bank).

#r "../src/PlaidClient/bin/Release/net8.0/PlaidClient.dll"

open System
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Plaid

let env name =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> failwith $"Set {name}"
    | value -> value

let optionalEnv name =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> ValueNone
    | value -> ValueSome value

let institution = optionalEnv "PLAID_INSTITUTION" |> ValueOption.defaultValue "ins_109508"
let http = new HttpClient()
let ct = CancellationToken.None

let webhookSiteAsync (method: HttpMethod) (url: string) =
    task {
        use request = new HttpRequestMessage(method, url)
        request.Headers.Add("Accept", "application/json")
        use! response = http.SendAsync(request, ct)
        response.EnsureSuccessStatusCode() |> ignore
        return! response.Content.ReadAsStringAsync()
    }

let createTokenAsync () =
    task {
        let! text = webhookSiteAsync HttpMethod.Post "https://webhook.site/token"
        use doc = JsonDocument.Parse text
        return doc.RootElement.GetProperty("uuid").GetString()
    }

/// One request webhook.site received: the body exactly as sent and the Plaid-Verification header.
type Received =
    { Body: string
      Jwt: string voption
      At: string }

let receivedAsync (token: string) =
    task {
        let! text = webhookSiteAsync HttpMethod.Get $"https://webhook.site/token/{token}/requests?sorting=oldest&per_page=100"
        use doc = JsonDocument.Parse text

        return
            doc.RootElement.GetProperty("data").EnumerateArray()
            |> Seq.map (fun r ->
                { Body = r.GetProperty("content").GetString()
                  Jwt =
                    match r.GetProperty("headers").TryGetProperty "plaid-verification" with
                    | true, values when values.GetArrayLength() > 0 -> ValueSome(values[0].GetString())
                    | _ -> ValueNone
                  At = r.GetProperty("created_at").GetString() })
            |> List.ofSeq
    }

/// "TYPE/CODE" of a webhook body, read without verifying it, only to know what has arrived.
let kindOf (body: string) =
    use doc = JsonDocument.Parse body
    let field (name: string) = doc.RootElement.GetProperty(name).GetString()
    $"""{field "webhook_type"}/{field "webhook_code"}"""

/// Polls until the requests after skip include every kind in expected, or deadline passes; returns them
/// with the kinds still missing.
let rec waitForAsync token (skip: int) (expected: Set<string>) (deadline: DateTime) =
    task {
        let! received = receivedAsync token
        let arrived = received |> List.skip skip
        let missing = expected - (arrived |> List.map (_.Body >> kindOf) |> Set.ofList)

        if missing.IsEmpty || DateTime.UtcNow > deadline then
            return arrived, missing
        else
            do! Task.Delay(TimeSpan.FromSeconds 3.0)
            return! waitForAsync token skip expected deadline
    }

let orFail label =
    function
    | Ok value -> value
    | Error e -> failwith $"{label}: %A{e}"

let report label (result: Result<'T, PlaidFailure>) =
    match result with
    | Ok value -> printfn $"OK    {label}: %A{value}"
    | Error e -> printfn $"FAIL  {label}: %A{e}"

let run () =
    task {
        let! token =
            match optionalEnv "WEBHOOK_SITE_TOKEN" with
            | ValueSome token -> Task.FromResult token
            | ValueNone -> createTokenAsync ()

        let webhookUrl = $"https://webhook.site/{token}"
        printfn $"Webhooks go to {webhookUrl}; open it in a browser to watch them arrive."

        let client =
            { Config =
                { PlaidConfig.uk (env "PLAID_CLIENT_ID") (env "PLAID_SECRET") PlaidEnvironment.Sandbox "PlaidClient webhook check" with
                    WebhookUrl = ValueSome webhookUrl }
              Http = new HttpClient() }

        let store = InMemoryPlaidStore() :> IPlaidStore
        let! before = receivedAsync token

        // The first sync inside linkItemAsync is what makes Plaid start sending SYNC_UPDATES_AVAILABLE.
        let! publicToken = Api.sandboxCreatePublicTokenAsync client institution ct
        let! linked = Sync.linkItemAsync client store "webhook-check" (orFail "public token" publicToken) ct
        let item = (orFail "link" linked).Item
        printfn $"Linked {item.ItemId}"

        let! fired = Api.sandboxFireWebhookAsync client item.AccessToken "TRANSACTIONS" "SYNC_UPDATES_AVAILABLE" ct
        report "fire SYNC_UPDATES_AVAILABLE" fired
        let! updated = Api.updateItemWebhookAsync client item.AccessToken (ValueSome webhookUrl) ct
        report "webhook update (WEBHOOK_UPDATE_ACKNOWLEDGED)" (updated |> Result.map _.ItemId)
        let! reset = Api.sandboxResetLoginAsync client item.AccessToken ct
        report "reset login (ITEM_LOGIN_REQUIRED)" reset

        // The three webhooks triggered above. In sandbox runs they arrived within ten seconds; the deadline
        // stays well inside Plaid's five-minute signature window, so they still verify at the current time.
        let expected =
            set [ "TRANSACTIONS/SYNC_UPDATES_AVAILABLE"; "ITEM/WEBHOOK_UPDATE_ACKNOWLEDGED"; "ITEM/ERROR" ]

        let! arrived, missing = waitForAsync token before.Length expected (DateTime.UtcNow.AddSeconds 90.0)
        printfn $"\n%d{arrived.Length} webhook(s) arrived:"

        let cache = Webhook.KeyCache(TimeSpan.FromHours 1.0)

        let check (r: Received) =
            task {
                match r.Jwt with
                | ValueNone ->
                    printfn $"NO SIGNATURE  {r.At}  {r.Body}"
                    return false
                | ValueSome jwt ->
                    let! result = Webhook.receiveCachedAsync cache client r.Body jwt DateTimeOffset.UtcNow ct
                    let status = Webhook.statusCodeFor result

                    match result with
                    | Ok(Webhook.ItemError(_, ValueSome error) as event) ->
                        printfn $"%d{status}  %s{r.At}  %A{event}\n     action: %A{PlaidApiError.action error}"
                    | Ok event -> printfn $"%d{status}  %s{r.At}  %A{event}"
                    | Error e -> printfn $"%d{status}  %s{r.At}  %A{e}\n     body: %s{r.Body}"

                    return Result.isOk result
            }

        let! verified = arrived |> List.map check |> Task.WhenAll
        let allVerified = missing.IsEmpty && Array.forall id verified

        if not missing.IsEmpty then
            let missingKinds = String.concat ", " missing
            printfn $"\nDid not arrive within the deadline: %s{missingKinds}"

        printfn (if allVerified then "\nAll expected webhooks arrived and verified." else "\nCheck failed.")
        return if allVerified then 0 else 1
    }

exit (run () |> Async.AwaitTask |> Async.RunSynchronously)
