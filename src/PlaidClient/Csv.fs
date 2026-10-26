/// Transactions as RFC 4180 CSV, e.g. for spreadsheets or bulk import.
module Plaid.Csv

open System
open System.Globalization
open System.IO
open System.Text

let private header =
    [ "transaction_id"
      "account_id"
      "date"
      "authorized_date"
      "name"
      "merchant_name"
      "amount"
      "iso_currency_code"
      "pending"
      "category_primary"
      "category_detailed"
      "payment_channel" ]

let private quote (field: string) =
    if field.IndexOfAny [| ','; '"'; '\r'; '\n' |] >= 0 then
        "\"" + field.Replace("\"", "\"\"") + "\""
    else
        field

/// Bank-supplied text starting with = + - @ would run as a spreadsheet formula; a leading apostrophe keeps it text.
let private text (field: string) =
    if field.Length > 0 && "=+-@\t\r".IndexOf field.[0] >= 0 then
        "'" + field
    else
        field

let private date (d: DateTime) =
    d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

let private row (t: Transaction) =
    let orEmpty = ValueOption.defaultValue ""

    [ t.TransactionId
      t.AccountId
      date t.Date
      t.AuthorizedDate |> ValueOption.map date |> orEmpty
      text t.Name
      t.MerchantName |> ValueOption.map text |> orEmpty
      t.Amount.ToString CultureInfo.InvariantCulture
      t.IsoCurrencyCode |> ValueOption.orElse t.UnofficialCurrencyCode |> orEmpty
      (if t.Pending then "true" else "false")
      t.Category |> ValueOption.map _.Primary |> orEmpty
      t.Category |> ValueOption.map _.Detailed |> orEmpty
      t.PaymentChannel ]

let private line fields =
    String.Join(",", fields |> List.map quote)

let ofTransactions (transactions: Transaction seq) =
    let lines = Seq.append [ line header ] (transactions |> Seq.map (row >> line))
    String.Join("\r\n", lines) + "\r\n"

let writeTransactions (path: string) (transactions: Transaction seq) =
    File.WriteAllText(path, ofTransactions transactions, UTF8Encoding(false))
