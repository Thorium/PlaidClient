module StorageTests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Plaid
open FakePlaid

let private ct = CancellationToken.None

let private parseTransaction = Transaction.ofRaw

let private tempFolder () =
    Path.Combine(Path.GetTempPath(), "plaidclient-tests", Guid.NewGuid().ToString "N")

let private item =
    { ItemId = "item-1"
      UserId = "user-42"
      AccessToken = "access-1"
      Cursor = ValueNone
      InstitutionId = ValueSome "ins_1"
      ConsentExpiration = ValueSome(DateTimeOffset(2027, 4, 1, 0, 0, 0, TimeSpan.Zero)) }

[<Fact>]
let ``json file store survives a new instance and keeps Plaid's raw fields`` () : Task =
    task {
        let folder = tempFolder ()

        try
            do!
                task {
                    use store = new JsonFilePlaidStore(folder)
                    let s = store :> IPlaidStore
                    do! s.SaveItemAsync(item, ct)

                    let changes =
                        { TransactionChanges.empty with
                            Added = [ parseTransaction (transactionJson "tx-2" 5m); parseTransaction (transactionJson "tx-1" 7.25m) ] }

                    do! s.ApplySyncAsync("item-1", changes, "cursor-1", ct)
                }

            use reopened = new JsonFilePlaidStore(folder)
            let s = reopened :> IPlaidStore
            let! stored = s.TryGetItemAsync("item-1", ct)
            Assert.Equal(ValueSome { item with Cursor = ValueSome "cursor-1" }, stored)

            let! transactions = s.GetTransactionsAsync("item-1", ct)
            Assert.Equal<string list>([ "tx-1"; "tx-2" ], transactions |> List.map _.TransactionId)
            Assert.Equal(7.25m, transactions.Head.Amount)
            Assert.Contains("\"transaction_code\":\"purchase\"", transactions.Head.Raw)

            do! s.DeleteItemAsync("item-1", ct)
            let! afterDelete = s.TryGetItemAsync("item-1", ct)
            Assert.Equal(ValueNone, afterDelete)
            Assert.False(File.Exists(Path.Combine(folder, "transactions", "item-1.json")))
        finally
            if Directory.Exists folder then
                Directory.Delete(folder, true)
    }

[<Fact>]
let ``json file store refuses item ids that are not plain file names`` () : Task =
    task {
        use store = new JsonFilePlaidStore(tempFolder ())

        let! _ =
            Assert.ThrowsAsync<ArgumentException>(fun () ->
                (store :> IPlaidStore).GetTransactionsAsync("..\\..\\x", ct) :> Task)

        ()
    }

[<Fact>]
let ``apply upserts then removes`` () =
    let t1 = parseTransaction (transactionJson "tx-1" 1m)
    let t1b = parseTransaction (transactionJson "tx-1" 2m)
    let t2 = parseTransaction (transactionJson "tx-2" 3m)

    let result =
        Map.empty
        |> TransactionChanges.apply
            { Added = [ t1; t2 ]
              Modified = [ t1b ]
              Removed = [ { TransactionId = "tx-2"; AccountId = ValueNone } ] }

    Assert.Equal<string list>([ "tx-1" ], result |> Map.toList |> List.map fst)
    Assert.Equal(2m, result["tx-1"].Amount)

[<Fact>]
let ``csv quotes fields and neutralises formulas in bank text`` () =
    let t = parseTransaction (transactionJson "tx-1" -12.5m)

    let tricky =
        { t with
            Name = "Smith, \"J\""
            MerchantName = ValueSome "=HYPERLINK(\"http://x\")" }

    let lines = (Csv.ofTransactions [ tricky ]).Split([| "\r\n" |], StringSplitOptions.RemoveEmptyEntries)

    Assert.Equal(2, lines.Length)
    Assert.StartsWith("transaction_id,account_id,date,", lines[0])

    Assert.Equal(
        "tx-1,acc-1,2026-10-01,2026-09-30,\"Smith, \"\"J\"\"\",\"'=HYPERLINK(\"\"http://x\"\")\",-12.5,GBP,false,FOOD_AND_DRINK,FOOD_AND_DRINK_GROCERIES,in store",
        lines[1]
    )
