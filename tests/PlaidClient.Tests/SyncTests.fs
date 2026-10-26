module SyncTests

open System.Threading
open System.Threading.Tasks
open Xunit
open Plaid
open FakePlaid

let private ct = CancellationToken.None

let private item =
    { ItemId = "item-1"
      UserId = "user-42"
      AccessToken = "access-1"
      Cursor = ValueNone
      InstitutionId = ValueNone
      ConsentExpiration = ValueNone }

let private storeWith items =
    let store = InMemoryPlaidStore() :> IPlaidStore

    task {
        for i in items do
            do! store.SaveItemAsync(i, ct)

        return store
    }

let private expectOk =
    function
    | Ok v -> v
    | Error e -> failwith $"%A{e}"

[<Fact>]
let ``sync pages through has_more and applies adds, modifies and removes in order`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (syncPageJson [ transactionJson "tx-1" 10m; transactionJson "tx-2" 20m ] [] [] "c1" true)
                | ValueSome "c1" -> ok (syncPageJson [ transactionJson "tx-3" 30m ] [ transactionJson "tx-1" 11m ] [ "tx-2" ] "c2" false)
                | ValueSome other -> failwith $"unexpected cursor {other}")

        let client = clientWith handler
        let! store = storeWith [ item ]
        let! summary = Sync.syncItemAsync client store "item-1" ct
        let summary = expectOk summary

        Assert.Equal(3, summary.Added)
        Assert.Equal(1, summary.Modified)
        Assert.Equal(1, summary.Removed)
        Assert.Equal(TransactionsUpdateStatus.HistoricalUpdateComplete, summary.UpdateStatus)

        let! transactions = store.GetTransactionsAsync("item-1", ct)
        Assert.Equal<string list>([ "tx-1"; "tx-3" ], transactions |> List.map _.TransactionId)
        Assert.Equal(11m, (transactions |> List.find (fun t -> t.TransactionId = "tx-1")).Amount)

        let! stored = store.TryGetItemAsync("item-1", ct)
        Assert.Equal(ValueSome "c2", stored |> ValueOption.bind _.Cursor)
    }

[<Fact>]
let ``next sync resumes from the stored cursor`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueSome "c2" -> ok (syncPageJson [] [] [] "c3" false)
                | other -> failwith $"unexpected cursor %A{other}")

        let! store = storeWith [ { item with Cursor = ValueSome "c2" } ]
        let! summary = Sync.syncItemAsync (clientWith handler) store "item-1" ct
        Assert.Equal(0, (expectOk summary).Added)
        let! stored = store.TryGetItemAsync("item-1", ct)
        Assert.Equal(ValueSome "c3", stored |> ValueOption.bind _.Cursor)
    }

[<Fact>]
let ``mutation during pagination restarts from the original cursor and drops the partial pages`` () : Task =
    task {
        let failedOnce = ref false

        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (syncPageJson [ transactionJson "tx-1" 10m ] [] [] "c1" true)
                | ValueSome "c1" when not failedOnce.Value ->
                    failedOnce.Value <- true
                    plaidError 400 "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION"
                | ValueSome "c1" -> ok (syncPageJson [ transactionJson "tx-2" 20m ] [] [] "c2" false)
                | ValueSome other -> failwith $"unexpected cursor {other}")

        let! store = storeWith [ item ]
        let! summary = Sync.syncItemAsync (clientWith handler) store "item-1" ct

        Assert.Equal(2, (expectOk summary).Added)

        Assert.Equal<string voption list>(
            [ ValueNone; ValueSome "c1"; ValueNone; ValueSome "c1" ],
            handler.Calls |> List.map (fun c -> cursorOf c.Body)
        )
    }

[<Fact>]
let ``repeated mutation during pagination stops after MaxRestarts and returns the error`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (syncPageJson [ transactionJson "tx-1" 10m ] [] [] "c1" true)
                | ValueSome _ -> plaidError 400 "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION")

        let client =
            { clientWith handler with
                Config = { config with Sync = { SyncSettings.defaults with MaxRestarts = 2 } } }

        let! store = storeWith [ item ]
        let! result = Sync.syncItemAsync client store "item-1" ct

        match result with
        | Error(ApiError e) -> Assert.Equal("TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION", e.ErrorCode)
        | other -> failwith $"%A{other}"

        // The first attempt plus two restarts, two pages each.
        Assert.Equal(6, handler.Calls.Length)
        let! stored = store.TryGetItemAsync("item-1", ct)
        Assert.Equal(ValueNone, stored |> ValueOption.bind _.Cursor)
        let! transactions = store.GetTransactionsAsync("item-1", ct)
        Assert.Empty transactions
    }

[<Fact>]
let ``MaxRestarts zero returns the first mutation error`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> plaidError 400 "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION")

        let client =
            { clientWith handler with
                Config = { config with Sync = { SyncSettings.defaults with MaxRestarts = 0 } } }

        let! result = Sync.fetchChangesAsync client "access-1" ValueNone ct
        Assert.True(Result.isError result)
        Assert.Single handler.Calls |> ignore
    }

[<Fact>]
let ``a failed sync leaves the cursor and transactions untouched`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (syncPageJson [ transactionJson "tx-1" 10m ] [] [] "c1" true)
                | ValueSome _ -> plaidError 400 "ITEM_LOGIN_REQUIRED")

        let! store = storeWith [ item ]
        let! result = Sync.syncItemAsync (clientWith handler) store "item-1" ct

        match result with
        | Error(ApiError e) -> Assert.Equal("ITEM_LOGIN_REQUIRED", e.ErrorCode)
        | other -> failwith $"%A{other}"

        let! transactions = store.GetTransactionsAsync("item-1", ct)
        Assert.Empty transactions
        let! stored = store.TryGetItemAsync("item-1", ct)
        Assert.Equal(ValueNone, stored |> ValueOption.bind _.Cursor)
    }

[<Fact>]
let ``sync of an unknown item is an error`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> failwith "no call expected")
        let! store = storeWith []
        let! result = Sync.syncItemAsync (clientWith handler) store "nope" ct
        Assert.Equal(Error(UnknownItem "nope"), result)
    }

let private notReadyPage =
    """{"accounts":[],"added":[],"modified":[],"removed":[],"next_cursor":"","has_more":false,"transactions_update_status":"NOT_READY","request_id":"r"}"""

[<Fact>]
let ``linking stores the item, reads consent expiry and runs the first sync so webhooks start`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun path _ ->
                match path with
                | "/item/public_token/exchange" -> ok """{"access_token":"access-9","item_id":"item-9","request_id":"r"}"""
                | "/accounts/get" ->
                    ok
                        $"""{{"accounts":[{accountJson}],"item":{{"item_id":"item-9","institution_id":"ins_7","consent_expiration_time":"2027-04-01T00:00:00Z"}},"request_id":"r"}}"""
                | "/transactions/sync" -> ok notReadyPage
                | other -> failwith other)

        let! store = storeWith []
        let! result = Sync.linkItemAsync (clientWith handler) store "user-42" "public-1" ct
        let linked = expectOk result

        Assert.Equal("access-9", linked.Item.AccessToken)
        Assert.Single linked.Accounts |> ignore
        Assert.Equal(TransactionsUpdateStatus.NotReady, linked.InitialSync.UpdateStatus)

        Assert.Equal<string list>(
            [ "/item/public_token/exchange"; "/accounts/get"; "/transactions/sync" ],
            handler.Calls |> List.map _.Path
        )

        let! items = store.GetUserItemsAsync("user-42", ct)
        let stored = Assert.Single items
        Assert.Equal(ValueSome "ins_7", stored.InstitutionId)
        Assert.True(stored.ConsentExpiration.IsSome)
    }

[<Fact>]
let ``an empty cursor from a NOT_READY sync is not sent back`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok notReadyPage)
        let! store = storeWith [ { item with Cursor = ValueSome "" } ]
        let! _ = Sync.syncItemAsync (clientWith handler) store "item-1" ct
        let call = Assert.Single handler.Calls
        Assert.False(call.Body.TryGetProperty "cursor" |> fst)
    }

[<Fact>]
let ``a failure after the token exchange keeps the item so the access token is not lost`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun path _ ->
                match path with
                | "/item/public_token/exchange" -> ok """{"access_token":"access-9","item_id":"item-9","request_id":"r"}"""
                | _ -> plaidError 500 "INTERNAL_SERVER_ERROR")

        let! store = storeWith []
        let! result = Sync.linkItemAsync (clientWith handler) store "user-42" "public-1" ct
        Assert.True(Result.isError result)
        let! stored = store.TryGetItemAsync("item-9", ct)
        Assert.Equal(ValueSome "access-9", stored |> ValueOption.map _.AccessToken)
    }

[<Fact>]
let ``unlinking an item Plaid no longer knows still deletes it locally`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> plaidError 400 "ITEM_NOT_FOUND")
        let! store = storeWith [ item ]
        let! result = Sync.unlinkItemAsync (clientWith handler) store "item-1" ct
        Assert.Equal(Ok(), result)
        let! stored = store.TryGetItemAsync("item-1", ct)
        Assert.Equal(ValueNone, stored)
    }

[<Fact>]
let ``unlinking removes the item at Plaid and from the store`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok """{"request_id":"r"}""")
        let! store = storeWith [ item ]
        let! result = Sync.unlinkItemAsync (clientWith handler) store "item-1" ct
        Assert.Equal(Ok(), result)
        Assert.Equal("/item/remove", (Assert.Single handler.Calls).Path)
        let! stored = store.TryGetItemAsync("item-1", ct)
        Assert.Equal(ValueNone, stored)
    }
