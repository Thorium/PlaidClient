// Links a Plaid sandbox institution without the Link UI, syncs its transactions into a
// JsonFilePlaidStore and writes them as CSV.
//
//   dotnet build -c Release
//   $env:PLAID_CLIENT_ID = "..."; $env:PLAID_SECRET = "<sandbox secret>"
//   dotnet fsi examples/sandbox.fsx
//
// PLAID_INSTITUTION picks the sandbox institution (default ins_109508, First Platypus Bank).

#r "../src/PlaidClient/bin/Release/net8.0/PlaidClient.dll"

open System
open System.IO
open System.Net.Http
open System.Threading
open Plaid

let env name =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> failwith $"Set {name}"
    | value -> value

let institution =
    match Environment.GetEnvironmentVariable "PLAID_INSTITUTION" with
    | null
    | "" -> "ins_109508"
    | value -> value

let client =
    { Config = PlaidConfig.uk (env "PLAID_CLIENT_ID") (env "PLAID_SECRET") PlaidEnvironment.Sandbox "PlaidClient sample"
      Http = new HttpClient() }

let dataFolder = Path.Combine(__SOURCE_DIRECTORY__, "data")
let store = new JsonFilePlaidStore(dataFolder)
let ct = CancellationToken.None

let orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"%A{e}"

// Plaid fills a new sandbox item in the background; without a webhook endpoint, poll until the first update lands.
let rec syncUntilReady itemId attemptsLeft =
    task {
        let! summary = Sync.syncItemAsync client store itemId ct
        let summary = orFail summary

        match summary.UpdateStatus with
        | TransactionsUpdateStatus.NotReady
        | TransactionsUpdateStatus.Unknown when attemptsLeft > 0 ->
            printfn "Transactions not ready yet, waiting..."
            do! Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 3.0)
            return! syncUntilReady itemId (attemptsLeft - 1)
        | _ -> return summary
    }

task {
    let! publicToken = Api.sandboxCreatePublicTokenAsync client institution ct
    let! linked = Sync.linkItemAsync client store "sample-user" (orFail publicToken) ct
    let linked = orFail linked
    let item = linked.Item
    printfn $"Linked {item.ItemId} with {linked.Accounts.Length} accounts ({linked.InitialSync.UpdateStatus})"

    let! summary = syncUntilReady item.ItemId 20
    printfn $"Sync: +{summary.Added} ~{summary.Modified} -{summary.Removed} ({summary.UpdateStatus})"

    let! transactions = (store :> IPlaidStore).GetTransactionsAsync(item.ItemId, ct)
    let csvPath = Path.Combine(dataFolder, $"{item.ItemId}.csv")
    Csv.writeTransactions csvPath transactions
    printfn $"Wrote {transactions.Length} transactions to {csvPath}"
}
|> Async.AwaitTask
|> Async.RunSynchronously
