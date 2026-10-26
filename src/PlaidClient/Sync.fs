/// Item lifecycle on top of Api and an IPlaidStore.
module Plaid.Sync

open System.Threading
open System.Threading.Tasks

type SyncSummary =
    { Added: int
      Modified: int
      Removed: int
      UpdateStatus: TransactionsUpdateStatus }

type LinkedItem =
    { Item: StoredItem
      Accounts: Account list
      /// The sync linking runs: often NotReady with nothing added yet.
      InitialSync: SyncSummary }

/// Every page after cursor. When Plaid reports TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION the
/// pages fetched so far are discarded and paging restarts from cursor; the token bounds the retries.
let fetchChangesAsync
    (client: PlaidClient)
    (accessToken: string)
    (cursor: string voption)
    (ct: CancellationToken)
    : Task<Result<TransactionChanges * SyncPage, PlaidFailure>> =
    let rec loop pageCursor (pages: SyncPage list) =
        task {
            match! Api.syncTransactionsPageAsync client accessToken pageCursor Api.MaxSyncPageSize ct with
            | Ok page when page.HasMore -> return! loop (ValueSome page.NextCursor) (page :: pages)
            | Ok page -> return Ok(TransactionChanges.ofPages (List.rev (page :: pages)), page)
            | Error(ApiError e) when e.ErrorCode = "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION" ->
                ct.ThrowIfCancellationRequested()
                return! loop cursor []
            | Error e -> return Error e
        }

    loop cursor []

/// Pulls everything since the item's stored cursor and applies it to the store.
let syncItemAsync (client: PlaidClient) (store: IPlaidStore) (itemId: string) ct =
    task {
        match! store.TryGetItemAsync(itemId, ct) with
        | ValueNone -> return Error(UnknownItem itemId)
        | ValueSome item ->
            match! fetchChangesAsync client item.AccessToken item.Cursor ct with
            | Error e -> return Error e
            | Ok(changes, lastPage) ->
                do! store.ApplySyncAsync(itemId, changes, lastPage.NextCursor, ct)

                return
                    Ok
                        { Added = changes.Added.Length
                          Modified = changes.Modified.Length
                          Removed = changes.Removed.Length
                          UpdateStatus = lastPage.UpdateStatus }
    }

/// Exchanges the public token from Link, stores the item, reads its accounts and runs the first sync.
/// Plaid sends SYNC_UPDATES_AVAILABLE for an item only after /transactions/sync has been called once.
/// The item is stored as soon as the exchange succeeds, so a later Error leaves it in the store:
/// retry with syncItemAsync rather than linking again.
let linkItemAsync (client: PlaidClient) (store: IPlaidStore) (userId: string) (publicToken: string) ct =
    task {
        match! Api.exchangePublicTokenAsync client publicToken ct with
        | Error e -> return Error e
        | Ok access ->
            let item =
                { ItemId = access.ItemId
                  UserId = userId
                  AccessToken = access.AccessToken
                  Cursor = ValueNone
                  InstitutionId = ValueNone
                  ConsentExpiration = ValueNone }

            do! store.SaveItemAsync(item, ct)

            match! Api.getAccountsAsync client access.AccessToken ct with
            | Error e -> return Error e
            | Ok accounts ->
                let item =
                    { item with
                        InstitutionId = accounts.Item.InstitutionId
                        ConsentExpiration = accounts.Item.ConsentExpirationTime }

                do! store.SaveItemAsync(item, ct)

                match! syncItemAsync client store item.ItemId ct with
                | Error e -> return Error e
                | Ok summary ->
                    let! synced = store.TryGetItemAsync(item.ItemId, ct)

                    return
                        Ok
                            { Item = synced |> ValueOption.defaultValue item
                              Accounts = accounts.Accounts
                              InitialSync = summary }
    }

/// Removes the item at Plaid, which ends its billing, then deletes it from the store.
/// An item Plaid no longer knows (ITEM_NOT_FOUND) is deleted from the store as well.
let unlinkItemAsync (client: PlaidClient) (store: IPlaidStore) (itemId: string) ct =
    task {
        match! store.TryGetItemAsync(itemId, ct) with
        | ValueNone -> return Error(UnknownItem itemId)
        | ValueSome item ->
            match! Api.removeItemAsync client item.AccessToken ct with
            | Ok()
            | Error(ApiError { ErrorCode = "ITEM_NOT_FOUND" }) ->
                do! store.DeleteItemAsync(itemId, ct)
                return Ok()
            | Error e -> return Error e
    }

/// Re-reads institution and consent expiry, e.g. after the user renewed consent in update mode.
let refreshItemInfoAsync (client: PlaidClient) (store: IPlaidStore) (itemId: string) ct =
    task {
        match! store.TryGetItemAsync(itemId, ct) with
        | ValueNone -> return Error(UnknownItem itemId)
        | ValueSome item ->
            match! Api.getAccountsAsync client item.AccessToken ct with
            | Error e -> return Error e
            | Ok accounts ->
                let updated =
                    { item with
                        InstitutionId = accounts.Item.InstitutionId
                        ConsentExpiration = accounts.Item.ConsentExpirationTime }

                do! store.SaveItemAsync(updated, ct)
                return Ok(updated, accounts)
    }
