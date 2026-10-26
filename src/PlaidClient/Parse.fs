/// Plaid response JSON to domain records.
module internal Plaid.Parse

open System.Text.Json
open Plaid.Json

/// httpStatus is the response status; error objects embedded in a 200 body carry their own "status".
let apiError httpStatus (e: JsonElement) : PlaidApiError =
    { HttpStatus =
        tryInt64 "status" e
        |> ValueOption.map int
        |> ValueOption.defaultValue httpStatus
      ErrorType = tryString "error_type" e |> ValueOption.defaultValue ""
      ErrorCode = required "error_code" tryString e
      ErrorMessage = tryString "error_message" e |> ValueOption.defaultValue ""
      DisplayMessage = tryString "display_message" e
      RequestId = tryString "request_id" e }

let linkToken (e: JsonElement) : LinkToken =
    { LinkToken = required "link_token" tryString e
      Expiration = required "expiration" tryTimestamp e
      RequestId = tryString "request_id" e |> ValueOption.defaultValue "" }

let itemAccess (e: JsonElement) : ItemAccess =
    { ItemId = required "item_id" tryString e
      AccessToken = required "access_token" tryString e }

let private balances (e: JsonElement) : Balances =
    { Available = tryDecimal "available" e
      Current = tryDecimal "current" e
      Limit = tryDecimal "limit" e
      IsoCurrencyCode = tryString "iso_currency_code" e
      UnofficialCurrencyCode = tryString "unofficial_currency_code" e }

let account (e: JsonElement) : Account =
    { AccountId = required "account_id" tryString e
      Name = tryString "name" e |> ValueOption.defaultValue ""
      OfficialName = tryString "official_name" e
      Mask = tryString "mask" e
      Type = tryString "type" e |> ValueOption.defaultValue ""
      Subtype = tryString "subtype" e
      Balances = required "balances" tryObject e |> balances }

let private itemInfo (e: JsonElement) : ItemInfo =
    { ItemId = required "item_id" tryString e
      InstitutionId = tryString "institution_id" e
      ConsentExpirationTime = tryTimestamp "consent_expiration_time" e
      Error =
        tryObject "error" e
        |> ValueOption.filter (fun err -> (tryString "error_code" err).IsSome)
        |> ValueOption.map (apiError 0) }

let accounts (e: JsonElement) : AccountsResult =
    { Item = required "item" tryObject e |> itemInfo
      Accounts = array "accounts" e |> List.map account }

let private category (e: JsonElement) : PersonalFinanceCategory =
    { Primary = tryString "primary" e |> ValueOption.defaultValue ""
      Detailed = tryString "detailed" e |> ValueOption.defaultValue ""
      ConfidenceLevel = tryString "confidence_level" e }

let transaction (e: JsonElement) : Transaction =
    { TransactionId = required "transaction_id" tryString e
      AccountId = required "account_id" tryString e
      Amount = required "amount" tryDecimal e
      IsoCurrencyCode = tryString "iso_currency_code" e
      UnofficialCurrencyCode = tryString "unofficial_currency_code" e
      Date = required "date" tryDate e
      AuthorizedDate = tryDate "authorized_date" e
      Name = tryString "name" e |> ValueOption.defaultValue ""
      OriginalDescription = tryString "original_description" e
      MerchantName = tryString "merchant_name" e
      Pending = tryBool "pending" e |> ValueOption.defaultValue false
      PendingTransactionId = tryString "pending_transaction_id" e
      PaymentChannel = tryString "payment_channel" e |> ValueOption.defaultValue ""
      Category = tryObject "personal_finance_category" e |> ValueOption.map category
      Raw = e.GetRawText() }

let private removed (e: JsonElement) : RemovedTransaction =
    { TransactionId = required "transaction_id" tryString e
      AccountId = tryString "account_id" e }

let private updateStatus =
    function
    | ValueSome "NOT_READY" -> TransactionsUpdateStatus.NotReady
    | ValueSome "INITIAL_UPDATE_COMPLETE" -> TransactionsUpdateStatus.InitialUpdateComplete
    | ValueSome "HISTORICAL_UPDATE_COMPLETE" -> TransactionsUpdateStatus.HistoricalUpdateComplete
    | _ -> TransactionsUpdateStatus.Unknown

let syncPage (e: JsonElement) : SyncPage =
    { Added = array "added" e |> List.map transaction
      Modified = array "modified" e |> List.map transaction
      Removed = array "removed" e |> List.map removed
      NextCursor = required "next_cursor" tryString e
      HasMore = required "has_more" tryBool e
      UpdateStatus = tryString "transactions_update_status" e |> updateStatus
      Accounts = array "accounts" e |> List.map account }

let webhookKey (e: JsonElement) : WebhookVerificationKey =
    let key = required "key" tryObject e

    { KeyId = required "kid" tryString key
      Algorithm = required "alg" tryString key
      Curve = required "crv" tryString key
      X = required "x" tryString key
      Y = required "y" tryString key
      ExpiredAt =
        tryInt64 "expired_at" key
        |> ValueOption.map System.DateTimeOffset.FromUnixTimeSeconds }
