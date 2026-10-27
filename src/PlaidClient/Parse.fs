/// Plaid response JSON to domain records.
module internal Plaid.Parse

open System.Text.Json
open Plaid.Json

/// httpStatus is the response status; error objects embedded in a 200 body carry their own "status".
let apiError httpStatus (e: JsonElement) : PlaidApiError =
    { HttpStatus = tryInt "status" e |> ValueOption.defaultValue httpStatus
      ErrorType = tryString "error_type" e |> ValueOption.defaultValue ""
      ErrorCode = requiredString "error_code" e
      ErrorMessage = tryString "error_message" e |> ValueOption.defaultValue ""
      DisplayMessage = tryString "display_message" e
      RequestId = tryString "request_id" e }

let linkToken (e: JsonElement) : LinkToken =
    { LinkToken = requiredString "link_token" e
      Expiration = required "expiration" tryTimestamp e
      RequestId = tryString "request_id" e |> ValueOption.defaultValue "" }

let itemAccess (e: JsonElement) : ItemAccess =
    { ItemId = requiredString "item_id" e
      AccessToken = requiredString "access_token" e }

let private balances (e: JsonElement) : Balances =
    { Available = tryDecimal "available" e
      Current = tryDecimal "current" e
      Limit = tryDecimal "limit" e
      IsoCurrencyCode = tryString "iso_currency_code" e
      UnofficialCurrencyCode = tryString "unofficial_currency_code" e
      LastUpdated = tryTimestamp "last_updated_datetime" e }

let private noBalances: Balances =
    { Available = ValueNone
      Current = ValueNone
      Limit = ValueNone
      IsoCurrencyCode = ValueNone
      UnofficialCurrencyCode = ValueNone
      LastUpdated = ValueNone }

/// Balances are empty where Plaid sends none (/identity/match returns them as null).
let account (e: JsonElement) : Account =
    { AccountId = requiredString "account_id" e
      Name = tryString "name" e |> ValueOption.defaultValue ""
      OfficialName = tryString "official_name" e
      Mask = tryString "mask" e
      Type = tryString "type" e |> ValueOption.defaultValue ""
      Subtype = tryString "subtype" e
      Balances = tryObject "balances" e |> ValueOption.map balances |> ValueOption.defaultValue noBalances }

let postalAddress (e: JsonElement) : PostalAddress =
    { Street = tryString "street" e
      City = tryString "city" e
      Region = tryString "region" e
      PostalCode = tryString "postal_code" e
      Country = tryString "country" e }

let private contact (e: JsonElement) : ContactDetail =
    { Data = tryString "data" e |> ValueOption.defaultValue ""
      Primary = tryBool "primary" e |> ValueOption.defaultValue false
      Type = tryString "type" e }

let owner (e: JsonElement) : Owner =
    { Names = strings "names" e
      Emails = array "emails" e |> List.map contact
      PhoneNumbers = array "phone_numbers" e |> List.map contact
      Addresses =
        array "addresses" e
        |> List.map (fun a ->
            { Data = tryObject "data" a |> ValueOption.map postalAddress |> ValueOption.defaultValue PostalAddress.empty
              Primary = tryBool "primary" a |> ValueOption.defaultValue false }) }

/// An error object inside a 200 reply or webhook; ValueNone when absent or without an error_code.
let embeddedError name (e: JsonElement) =
    tryObject name e
    |> ValueOption.filter (fun err -> (tryString "error_code" err).IsSome)
    |> ValueOption.map (apiError 0)

let private itemInfo (e: JsonElement) : ItemInfo =
    { ItemId = requiredString "item_id" e
      InstitutionId = tryString "institution_id" e
      InstitutionName = tryString "institution_name" e
      ConsentExpirationTime = tryTimestamp "consent_expiration_time" e
      Error = embeddedError "error" e
      Webhook = tryString "webhook" e
      Products = strings "products" e
      BilledProducts = strings "billed_products" e
      AvailableProducts = strings "available_products" e
      ConsentedProducts = strings "consented_products" e
      UpdateType = tryString "update_type" e }

/// A reply with an "item" object, e.g. /item/webhook/update.
let item (e: JsonElement) = required "item" tryObject e |> itemInfo

let accounts (e: JsonElement) : AccountsResult =
    { Item = item e
      Accounts = array "accounts" e |> List.map account }

let private itemStatus (status: JsonElement voption) : ItemStatus =
    let section name = status |> ValueOption.bind (tryObject name)
    let transactions = section "transactions"
    let lastWebhook = section "last_webhook"

    { TransactionsLastSuccessfulUpdate = transactions |> ValueOption.bind (tryTimestamp "last_successful_update")
      TransactionsLastFailedUpdate = transactions |> ValueOption.bind (tryTimestamp "last_failed_update")
      LastWebhookSentAt = lastWebhook |> ValueOption.bind (tryTimestamp "sent_at")
      LastWebhookCode = lastWebhook |> ValueOption.bind (tryString "code_sent") }

let itemDetails (e: JsonElement) : ItemDetails =
    { Item = item e
      Status = tryObject "status" e |> itemStatus }

let private institution (e: JsonElement) : Institution =
    { InstitutionId = requiredString "institution_id" e
      Name = tryString "name" e |> ValueOption.defaultValue ""
      Products = strings "products" e
      CountryCodes = strings "country_codes" e
      Url = tryString "url" e
      PrimaryColor = tryString "primary_color" e
      Logo = tryString "logo" e
      Oauth = tryBool "oauth" e |> ValueOption.defaultValue false }

let institutionById (e: JsonElement) = required "institution" tryObject e |> institution

let institutions (e: JsonElement) = array "institutions" e |> List.map institution

let private category (e: JsonElement) : PersonalFinanceCategory =
    { Primary = tryString "primary" e |> ValueOption.defaultValue ""
      Detailed = tryString "detailed" e |> ValueOption.defaultValue ""
      ConfidenceLevel = tryString "confidence_level" e }

let transaction (e: JsonElement) : Transaction =
    { TransactionId = requiredString "transaction_id" e
      AccountId = requiredString "account_id" e
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
    { TransactionId = requiredString "transaction_id" e
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
      NextCursor = requiredString "next_cursor" e
      HasMore = required "has_more" tryBool e
      UpdateStatus = tryString "transactions_update_status" e |> updateStatus
      Accounts = array "accounts" e |> List.map account }

let webhookKey (e: JsonElement) : WebhookVerificationKey =
    let key = required "key" tryObject e

    { KeyId = requiredString "kid" key
      Algorithm = requiredString "alg" key
      Curve = requiredString "crv" key
      X = requiredString "x" key
      Y = requiredString "y" key
      ExpiredAt =
        tryInt64 "expired_at" key
        |> ValueOption.map System.DateTimeOffset.FromUnixTimeSeconds }
