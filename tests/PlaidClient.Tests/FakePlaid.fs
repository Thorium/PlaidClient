module FakePlaid

open System
open System.Globalization
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Plaid

type Call =
    { Path: string
      Body: JsonElement
      Headers: Map<string, string> }

/// Answers each POST with respond path body, and records the calls.
type FakePlaidHandler(respond: string -> JsonElement -> int * string) =
    inherit HttpMessageHandler()
    let mutable calls: Call list = []

    member _.Calls = List.rev calls

    override _.SendAsync(request, ct) =
        task {
            ct.ThrowIfCancellationRequested()
            let! text = request.Content.ReadAsStringAsync()
            let body = JsonDocument.Parse(text).RootElement.Clone()
            let path = request.RequestUri.AbsolutePath

            let headers =
                request.Headers
                |> Seq.map (fun h -> h.Key, String.Join(",", h.Value))
                |> Map.ofSeq

            calls <- { Path = path; Body = body; Headers = headers } :: calls
            let status, json = respond path body

            return
                new HttpResponseMessage(
                    enum<HttpStatusCode> status,
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                )
        }

let config =
    { PlaidConfig.uk "client-1" "secret-1" PlaidEnvironment.Sandbox "Test Co" with
        RedirectUri = ValueSome "https://example.test/oauth" }

let clientWith (handler: FakePlaidHandler) =
    { Config = config
      Http = new HttpClient(handler) }

let ok json = 200, json

let plaidError status code =
    status,
    $"""{{"error_type":"ITEM_ERROR","error_code":"{code}","error_message":"message for {code}","display_message":"Please reconnect","request_id":"req-1"}}"""

let transactionJson (id: string) (amount: decimal) =
    let amountText = amount.ToString CultureInfo.InvariantCulture

    $"""{{"transaction_id":"{id}","account_id":"acc-1","amount":{amountText},"iso_currency_code":"GBP","unofficial_currency_code":null,"date":"2026-10-01","authorized_date":"2026-09-30","name":"TESCO STORES 1234","merchant_name":"Tesco","pending":false,"pending_transaction_id":null,"payment_channel":"in store","personal_finance_category":{{"primary":"FOOD_AND_DRINK","detailed":"FOOD_AND_DRINK_GROCERIES","confidence_level":"VERY_HIGH"}},"transaction_code":"purchase"}}"""

let accountJson =
    """{"account_id":"acc-1","name":"Current Account","official_name":null,"mask":"1234","type":"depository","subtype":"checking","balances":{"available":120.5,"current":130.25,"limit":null,"iso_currency_code":"GBP","unofficial_currency_code":null}}"""

let syncPageJson (added: string list) (modified: string list) (removed: string list) (nextCursor: string) (hasMore: bool) =
    let removedJson =
        removed |> List.map (fun pid -> $"""{{"transaction_id":"{pid}","account_id":"acc-1"}}""")

    let join (xs: string list) = String.Join(",", xs)
    let more = if hasMore then "true" else "false"

    $"""{{"accounts":[{accountJson}],"added":[{join added}],"modified":[{join modified}],"removed":[{join removedJson}],"next_cursor":"{nextCursor}","has_more":{more},"transactions_update_status":"HISTORICAL_UPDATE_COMPLETE","request_id":"req-s"}}"""

let cursorOf (body: JsonElement) =
    match body.TryGetProperty "cursor" with
    | true, v when v.ValueKind = JsonValueKind.String -> ValueSome(v.GetString())
    | _ -> ValueNone
