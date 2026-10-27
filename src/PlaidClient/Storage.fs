namespace Plaid

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// An item one end user linked, as the application keeps it. Printing it hides the access token.
[<StructuredFormatDisplay("{Display}")>]
type StoredItem =
    { ItemId: string
      /// The application's id for the end user, the clientUserId given to Link.
      UserId: string
      /// Long-lived secret: encrypt it at rest in a real store.
      AccessToken: string
      /// /transactions/sync cursor; ValueNone until a sync returned one.
      Cursor: string voption
      InstitutionId: string voption
      ConsentExpiration: DateTimeOffset voption }

    member this.Display =
        $"StoredItem {{ ItemId = %s{this.ItemId}; UserId = %s{this.UserId}; AccessToken = ***; Cursor = %A{this.Cursor} }}"

    override this.ToString() = this.Display

/// Persistence the Plaid flows need. Applications implement it over their own database;
/// JsonFilePlaidStore and InMemoryPlaidStore are for development and tests.
type IPlaidStore =
    abstract SaveItemAsync: item: StoredItem * ct: CancellationToken -> Task
    abstract TryGetItemAsync: itemId: string * ct: CancellationToken -> Task<StoredItem voption>
    abstract GetUserItemsAsync: userId: string * ct: CancellationToken -> Task<StoredItem list>
    /// Deletes the item and its transactions.
    abstract DeleteItemAsync: itemId: string * ct: CancellationToken -> Task
    /// Applies one completed sync and stores nextCursor, atomically: changes and cursor in one database
    /// transaction. Two syncs of the same item may overlap (a webhook and a manual sync), and only an
    /// atomic apply keeps the data matching the cursor. Without transactions, store the changes before the
    /// cursor and run one sync per item at a time.
    abstract ApplySyncAsync:
        itemId: string * changes: TransactionChanges * nextCursor: string * ct: CancellationToken -> Task
    abstract GetTransactionsAsync: itemId: string * ct: CancellationToken -> Task<Transaction list>

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Transaction =

    /// Rebuilds a Transaction from its Raw JSON, for stores that keep only Raw.
    let ofRaw (raw: string) =
        use doc = JsonDocument.Parse raw
        Parse.transaction doc.RootElement

module internal StoreJson =

    let toVOption =
        function
        | Some x -> ValueSome x
        | None -> ValueNone

    let private indented =
        JsonSerializerOptions(Request.serializerOptions, WriteIndented = true)

    let writeItems (items: StoredItem seq) =
        let dto (i: StoredItem) =
            {| item_id = i.ItemId
               user_id = i.UserId
               access_token = i.AccessToken
               cursor = i.Cursor
               institution_id = i.InstitutionId
               consent_expiration = i.ConsentExpiration |> ValueOption.map _.ToString("o") |}

        JsonSerializer.Serialize(items |> Seq.map dto |> List.ofSeq, indented)

    let readItems (text: string) =
        use doc = JsonDocument.Parse text

        doc.RootElement.EnumerateArray()
        |> Seq.map (fun e ->
            { ItemId = Json.requiredString "item_id" e
              UserId = Json.requiredString "user_id" e
              AccessToken = Json.requiredString "access_token" e
              Cursor = Json.tryString "cursor" e
              InstitutionId = Json.tryString "institution_id" e
              ConsentExpiration = Json.tryTimestamp "consent_expiration" e })
        |> List.ofSeq

    /// Plaid's own objects, so the file keeps every field Plaid sent.
    let writeTransactions (transactions: Transaction seq) =
        "[\n" + String.Join(",\n", transactions |> Seq.map _.Raw) + "\n]"

    let readTransactions (text: string) =
        use doc = JsonDocument.Parse text
        doc.RootElement.EnumerateArray() |> Seq.map Parse.transaction |> List.ofSeq

    let ordered (transactions: Map<string, Transaction>) =
        transactions
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun t -> t.Date, t.TransactionId)

/// Keeps everything in process memory.
type InMemoryPlaidStore() =
    let gate = obj ()
    let mutable items: Map<string, StoredItem> = Map.empty
    let mutable transactions: Map<string, Map<string, Transaction>> = Map.empty

    let locked f = lock gate f

    interface IPlaidStore with
        member _.SaveItemAsync(item, _) =
            locked (fun () -> items <- items |> Map.add item.ItemId item)
            Task.CompletedTask

        member _.TryGetItemAsync(itemId, _) =
            locked (fun () -> items |> Map.tryFind itemId |> StoreJson.toVOption)
            |> Task.FromResult

        member _.GetUserItemsAsync(userId, _) =
            locked (fun () -> items |> Map.toList |> List.map snd |> List.filter (fun i -> i.UserId = userId))
            |> Task.FromResult

        member _.DeleteItemAsync(itemId, _) =
            locked (fun () ->
                items <- items |> Map.remove itemId
                transactions <- transactions |> Map.remove itemId)

            Task.CompletedTask

        member _.ApplySyncAsync(itemId, changes, nextCursor, _) =
            locked (fun () ->
                let existing = transactions |> Map.tryFind itemId |> Option.defaultValue Map.empty
                transactions <- transactions |> Map.add itemId (TransactionChanges.apply changes existing)

                items <-
                    items
                    |> Map.change itemId (Option.map (fun i -> { i with Cursor = ValueSome nextCursor })))

            Task.CompletedTask

        member _.GetTransactionsAsync(itemId, _) =
            locked (fun () ->
                transactions
                |> Map.tryFind itemId
                |> Option.map StoreJson.ordered
                |> Option.defaultValue [])
            |> Task.FromResult

/// Files under folder: items.json for items, transactions/{itemId}.json for each item's transactions.
/// Access tokens are written in plain text, so this store is for development only.
type JsonFilePlaidStore(folder: string) =
    let gate = new SemaphoreSlim(1, 1)
    let itemsPath = Path.Combine(folder, "items.json")

    // Item ids become file names.
    let safeId = Regex("^[A-Za-z0-9_-]+$")

    let transactionsPath (itemId: string) =
        if not (safeId.IsMatch itemId) then
            invalidArg (nameof itemId) $"Item id '{itemId}' is not a safe file name."

        Path.Combine(folder, "transactions", itemId + ".json")

    let readText (path: string) =
        task {
            if not (File.Exists path) then
                return ValueNone
            else
                use reader = new StreamReader(path)
                let! text = reader.ReadToEndAsync()
                return ValueSome text
        }

    // Write then rename so a crash never leaves a half-written file.
    let writeText (path: string) (text: string) (ct: CancellationToken) =
        task {
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let temp = path + ".tmp"

            do!
                task {
                    use writer = new StreamWriter(temp)
                    do! writer.WriteAsync text
                }

            ct.ThrowIfCancellationRequested()

            if File.Exists path then
                File.Replace(temp, path, null)
            else
                File.Move(temp, path)
        }

    let readItems () =
        task {
            let! text = readText itemsPath
            return text |> ValueOption.map StoreJson.readItems |> ValueOption.defaultValue []
        }

    let writeItems items ct =
        writeText itemsPath (StoreJson.writeItems items) ct

    let readTransactions itemId =
        task {
            let! text = readText (transactionsPath itemId)

            return
                text
                |> ValueOption.map StoreJson.readTransactions
                |> ValueOption.defaultValue []
        }

    let locked (ct: CancellationToken) (f: unit -> Task<'T>) =
        task {
            do! gate.WaitAsync ct

            try
                return! f ()
            finally
                gate.Release() |> ignore
        }

    member _.Folder = folder

    interface IPlaidStore with
        member _.SaveItemAsync(item, ct) =
            locked ct (fun () ->
                task {
                    let! items = readItems ()
                    let others = items |> List.filter (fun i -> i.ItemId <> item.ItemId)
                    do! writeItems (others @ [ item ]) ct
                })

        member _.TryGetItemAsync(itemId, ct) =
            locked ct (fun () ->
                task {
                    let! items = readItems ()
                    return items |> List.tryFind (fun i -> i.ItemId = itemId) |> StoreJson.toVOption
                })

        member _.GetUserItemsAsync(userId, ct) =
            locked ct (fun () ->
                task {
                    let! items = readItems ()
                    return items |> List.filter (fun i -> i.UserId = userId)
                })

        member _.DeleteItemAsync(itemId, ct) =
            locked ct (fun () ->
                task {
                    let path = transactionsPath itemId
                    let! items = readItems ()
                    do! writeItems (items |> List.filter (fun i -> i.ItemId <> itemId)) ct

                    if File.Exists path then
                        File.Delete path
                })

        member _.ApplySyncAsync(itemId, changes, nextCursor, ct) =
            locked ct (fun () ->
                task {
                    let! existing = readTransactions itemId

                    let updated =
                        existing
                        |> List.map (fun t -> t.TransactionId, t)
                        |> Map.ofList
                        |> TransactionChanges.apply changes

                    do! writeText (transactionsPath itemId) (StoreJson.writeTransactions (StoreJson.ordered updated)) ct

                    let! items = readItems ()

                    let withCursor =
                        items
                        |> List.map (fun i -> if i.ItemId = itemId then { i with Cursor = ValueSome nextCursor } else i)

                    do! writeItems withCursor ct
                })

        member _.GetTransactionsAsync(itemId, ct) =
            locked ct (fun () -> readTransactions itemId)

    interface IDisposable with
        member _.Dispose() = gate.Dispose()
