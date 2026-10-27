/// Payment Initiation (Payments (Europe)): one-off payments and standing orders from the payer's bank
/// (UK and EU), and Variable Recurring Payments consents (UK).
///
/// Payment Initiation itself does not check who owns the paying account. For an account-ownership check,
/// link the payer's bank with Auth and Identity, confirm the holder with Identity.matchAsync, and restrict
/// the payment to that account with PayerAccount.ofAuth; for KYC, use Identity Verification.
module Plaid.Payments

open System
open System.Text.Json
open System.Threading.Tasks

/// Two-way mapping between DU cases and Plaid's codes, from one table.
type private Codes<'T when 'T: comparison>(table: ('T * string) list) =
    let byCode = table |> List.map (fun (value, code) -> code, value) |> Map.ofList
    let byValue = Map.ofList table

    member _.TryValue(code: string) = byCode |> Map.tryFind code
    member _.Code(value: 'T) = byValue |> Map.find value

type Money =
    { /// GBP, EUR, PLN, SEK, DKK or NOK.
      Currency: string
      /// At least 1, at most two decimals.
      Value: decimal }

/// UK account number and sort code. Printing shows only the last four digits of the account number.
[<StructuredFormatDisplay("{Display}")>]
type BacsAccount =
    { Account: string
      SortCode: string }

    member this.Display =
        $"BacsAccount {{ Account = %s{Mask.number this.Account}; SortCode = %s{this.SortCode} }}"

    override this.ToString() = this.Display

/// The payer's account. Printing masks the numbers.
[<StructuredFormatDisplay("{Display}")>]
type PayerAccount =
    | PayerIban of iban: string
    | PayerBacs of BacsAccount

    member this.Display =
        match this with
        | PayerIban iban -> $"PayerIban %s{Mask.number iban}"
        | PayerBacs bacs -> $"PayerBacs %s{bacs.Display}"

    override this.ToString() = this.Display

module PayerAccount =

    /// The UK (BACS) or EU (IBAN) numbers Auth reported for accountId: pass it as
    /// PaymentRequest.PayerAccount to restrict the payment to the account Identity Match verified.
    let ofAuth (numbers: Auth.AccountNumbers) (accountId: string) : PayerAccount voption =
        match
            numbers.Bacs |> List.tryFind (fun b -> b.AccountId = accountId),
            numbers.International |> List.tryFind (fun i -> i.AccountId = accountId)
        with
        | Some bacs, _ ->
            ValueSome(
                PayerBacs
                    { Account = bacs.Account
                      SortCode = bacs.SortCode }
            )
        | None, Some international -> ValueSome(PayerIban international.Iban)
        | None, None -> ValueNone

let private bacsBody (bacs: BacsAccount) =
    {| account = bacs.Account
       sort_code = bacs.SortCode |}

/// The iban and bacs request fields for a payer account; the one not used is left out.
let private payerFields (account: PayerAccount voption) =
    match account with
    | ValueSome(PayerIban iban) -> ValueSome iban, ValueNone
    | ValueSome(PayerBacs bacs) -> ValueNone, ValueSome(bacsBody bacs)
    | ValueNone -> ValueNone, ValueNone

let private money (m: Money) = {| currency = m.Currency; value = m.Value |}

let private parseMoney (e: JsonElement) : Money =
    { Currency = Json.requiredString "currency" e
      Value = Json.required "value" Json.tryDecimal e }

let private parseBacs (e: JsonElement) : BacsAccount =
    { Account = Json.requiredString "account" e
      SortCode = Json.requiredString "sort_code" e }

/// A field holding one of codes; ValueNone when absent, an InvalidResponse when unknown.
let private tryCode (codes: Codes<'T>) name e =
    Json.tryString name e
    |> ValueOption.map (fun code ->
        match codes.TryValue code with
        | Some value -> value
        | None -> raise (Json.InvalidJson $"Unknown {name} '{code}'"))

/// Plaid's rule for a payment or consent reference: 1-18 letters, digits and spaces.
let isValidReference (reference: string) =
    not (String.IsNullOrEmpty reference)
    && reference.Length <= 18
    && reference |> Seq.forall (fun c -> Char.IsLetterOrDigit c && c < '\u0080' || c = ' ')

/// Runs call only for a valid reference, so Plaid is not asked to create something it will refuse.
let private withReference (reference: string) (call: unit -> Task<Result<'T, PlaidFailure>>) =
    if isValidReference reference then
        call ()
    else
        Task.FromResult(Error(InvalidArgument $"Reference '{reference}' must be 1-18 letters, digits or spaces."))

// ---- Recipients ----

/// The payee's account: an IBAN, UK numbers, or both. GBP domestic payments need the UK numbers.
type RecipientAccount =
    | RecipientIban of iban: string
    | RecipientBacs of BacsAccount
    | RecipientIbanAndBacs of iban: string * bacs: BacsAccount

type RecipientAddress =
    { /// One or two lines, each at most 70 characters.
      Street: string list
      City: string
      PostalCode: string
      /// ISO 3166-1 alpha-2.
      Country: string }

type Recipient =
    { RecipientId: string
      Name: string
      Account: RecipientAccount
      Address: RecipientAddress voption }

/// Registers the payee (your account). Banks outside the UK mostly require the address.
/// Returns the recipient_id.
let createRecipientAsync
    (client: PlaidClient)
    (name: string)
    (account: RecipientAccount)
    (address: RecipientAddress voption)
    ct
    =
    let iban, bacs =
        match account with
        | RecipientIban iban -> ValueSome iban, ValueNone
        | RecipientBacs bacs -> ValueNone, ValueSome(bacsBody bacs)
        | RecipientIbanAndBacs(iban, bacs) -> ValueSome iban, ValueSome(bacsBody bacs)

    let body =
        {| name = name
           iban = iban
           bacs = bacs
           address =
            address
            |> ValueOption.map (fun a ->
                {| street = a.Street
                   city = a.City
                   postal_code = a.PostalCode
                   country = a.Country |}) |}

    Api.postAsync client "/payment_initiation/recipient/create" body (Json.requiredString "recipient_id") ct

let getRecipientAsync (client: PlaidClient) (recipientId: string) ct =
    let parse (e: JsonElement) : Recipient =
        { RecipientId = Json.requiredString "recipient_id" e
          Name = Json.requiredString "name" e
          Account =
            match Json.tryString "iban" e, Json.tryObject "bacs" e with
            | ValueSome iban, ValueSome bacs -> RecipientIbanAndBacs(iban, parseBacs bacs)
            | ValueSome iban, ValueNone -> RecipientIban iban
            | ValueNone, ValueSome bacs -> RecipientBacs(parseBacs bacs)
            | ValueNone, ValueNone -> raise (Json.InvalidJson "Recipient has neither iban nor bacs")
          Address =
            Json.tryObject "address" e
            |> ValueOption.map (fun a ->
                { Street = Json.strings "street" a
                  City = Json.requiredString "city" a
                  PostalCode = Json.requiredString "postal_code" a
                  Country = Json.requiredString "country" a }) }

    Api.postAsync client "/payment_initiation/recipient/get" {| recipient_id = recipientId |} parse ct

// ---- Payments ----

[<RequireQualifiedAccess>]
type PaymentStatus =
    /// Waiting for the payer in Link; can come back after Authorising.
    | InputNeeded
    | Authorising
    /// Accepted by the payer's bank; the final success status in many EU markets.
    | Initiated
    /// Funds left the payer's account; arrival is not confirmed.
    | Executed
    /// Funds arrived (virtual accounts only).
    | Settled
    /// A standing order was set up.
    | Established
    | InsufficientFunds
    | Rejected
    | Cancelled
    /// A system error; the payment can be tried again.
    | Failed
    /// Blocked by Plaid, e.g. as risky; the payment can be tried again.
    | Blocked
    /// A status this client does not know yet.
    | Other of string

module PaymentStatus =

    let private codes =
        Codes(
            [ PaymentStatus.InputNeeded, "PAYMENT_STATUS_INPUT_NEEDED"
              PaymentStatus.Authorising, "PAYMENT_STATUS_AUTHORISING"
              PaymentStatus.Initiated, "PAYMENT_STATUS_INITIATED"
              PaymentStatus.Executed, "PAYMENT_STATUS_EXECUTED"
              PaymentStatus.Settled, "PAYMENT_STATUS_SETTLED"
              PaymentStatus.Established, "PAYMENT_STATUS_ESTABLISHED"
              PaymentStatus.InsufficientFunds, "PAYMENT_STATUS_INSUFFICIENT_FUNDS"
              PaymentStatus.Rejected, "PAYMENT_STATUS_REJECTED"
              PaymentStatus.Cancelled, "PAYMENT_STATUS_CANCELLED"
              PaymentStatus.Failed, "PAYMENT_STATUS_FAILED"
              PaymentStatus.Blocked, "PAYMENT_STATUS_BLOCKED" ]
        )

    let ofString (code: string) =
        codes.TryValue code |> Option.defaultValue (PaymentStatus.Other code)

    let toString =
        function
        | PaymentStatus.Other code -> code
        | status -> codes.Code status

    /// The payer's bank accepted the payment (or set up the standing order).
    let isSuccessful =
        function
        | PaymentStatus.Initiated
        | PaymentStatus.Executed
        | PaymentStatus.Settled
        | PaymentStatus.Established -> true
        | _ -> false

    /// The payment ended without being paid.
    let isFailed =
        function
        | PaymentStatus.InsufficientFunds
        | PaymentStatus.Rejected
        | PaymentStatus.Cancelled
        | PaymentStatus.Failed
        | PaymentStatus.Blocked -> true
        | _ -> false

[<RequireQualifiedAccess>]
type ScheduleInterval =
    | Weekly
    | Monthly

let private intervals =
    Codes([ ScheduleInterval.Weekly, "WEEKLY"; ScheduleInterval.Monthly, "MONTHLY" ])

/// A standing order (GBP only).
type Schedule =
    { Interval: ScheduleInterval
      /// Weekly 1-7 (Monday = 1); monthly 1-28, or -1..-5 counting back from the month end.
      IntervalExecutionDay: int
      StartDate: DateTime
      EndDate: DateTime voption }

[<RequireQualifiedAccess>]
type Scheme =
    | LocalDefault
    | LocalInstant
    | SepaCreditTransfer
    | SepaCreditTransferInstant

let private schemes =
    Codes(
        [ Scheme.LocalDefault, "LOCAL_DEFAULT"
          Scheme.LocalInstant, "LOCAL_INSTANT"
          Scheme.SepaCreditTransfer, "SEPA_CREDIT_TRANSFER"
          Scheme.SepaCreditTransferInstant, "SEPA_CREDIT_TRANSFER_INSTANT" ]
    )

type PaymentRequest =
    { RecipientId: string
      /// 1-18 letters, digits and spaces; unique per payment.
      Reference: string
      Amount: Money
      /// user_id from Users.createAsync; required for integrations created from 2025-12-10 on.
      UserId: string voption
      Schedule: Schedule voption
      /// Restricts the payment to this payer account where the bank supports it.
      PayerAccount: PayerAccount voption
      /// ValueNone for the region's default.
      Scheme: Scheme voption
      /// Ask the payer's bank for refund details.
      RequestRefundDetails: bool }

module PaymentRequest =

    let create recipientId reference amount =
        { RecipientId = recipientId
          Reference = reference
          Amount = amount
          UserId = ValueNone
          Schedule = ValueNone
          PayerAccount = ValueNone
          Scheme = ValueNone
          RequestRefundDetails = false }

type PaymentCreated =
    { PaymentId: string
      Status: PaymentStatus }

let private schedule (s: Schedule) =
    {| interval = intervals.Code s.Interval
       interval_execution_day = s.IntervalExecutionDay
       start_date = Request.isoDate s.StartDate
       end_date = s.EndDate |> ValueOption.map Request.isoDate |}

let private paymentCreated (e: JsonElement) : PaymentCreated =
    { PaymentId = Json.requiredString "payment_id" e
      Status = Json.requiredCode "status" PaymentStatus.ofString e }

/// Creates the payment; the payer then authorises it in Link (createPaymentLinkTokenAsync).
let createPaymentAsync (client: PlaidClient) (request: PaymentRequest) ct =
    withReference request.Reference (fun () ->
        let options =
            match request.PayerAccount, request.Scheme, request.RequestRefundDetails with
            | ValueNone, ValueNone, false -> ValueNone
            | payer, scheme, refundDetails ->
                let iban, bacs = payerFields payer

                ValueSome
                    {| request_refund_details = Request.flag refundDetails
                       iban = iban
                       bacs = bacs
                       scheme = scheme |> ValueOption.map schemes.Code |}

        let body =
            {| recipient_id = request.RecipientId
               reference = request.Reference
               amount = money request.Amount
               user_id = request.UserId
               schedule = request.Schedule |> ValueOption.map schedule
               options = options |}

        Api.postAsync client "/payment_initiation/payment/create" body paymentCreated ct)

type Payment =
    { PaymentId: string
      Status: PaymentStatus
      LastStatusUpdate: DateTimeOffset voption
      RecipientId: string voption
      Reference: string voption
      /// The reference the bank actually used, when Plaid had to change it.
      AdjustedReference: string voption
      Amount: Money voption
      Schedule: Schedule voption
      PayerAccount: PayerAccount voption
      Scheme: Scheme voption
      ConsentId: string voption
      EndToEndId: string voption
      RefundIds: string list
      Error: PlaidApiError voption
      Raw: string }

let private payment (e: JsonElement) : Payment =
    { PaymentId = Json.requiredString "payment_id" e
      Status = Json.requiredCode "status" PaymentStatus.ofString e
      LastStatusUpdate = Json.tryTimestamp "last_status_update" e
      RecipientId = Json.tryString "recipient_id" e
      Reference = Json.tryString "reference" e
      AdjustedReference = Json.tryString "adjusted_reference" e
      Amount = Json.tryObject "amount" e |> ValueOption.map parseMoney
      Schedule =
        Json.tryObject "schedule" e
        |> ValueOption.map (fun s ->
            { Interval = Json.required "interval" (tryCode intervals) s
              IntervalExecutionDay = Json.required "interval_execution_day" Json.tryInt s
              StartDate = Json.required "start_date" Json.tryDate s
              EndDate = Json.tryDate "end_date" s })
      PayerAccount =
        match Json.tryObject "bacs" e, Json.tryString "iban" e with
        | ValueSome bacs, _ -> ValueSome(PayerBacs(parseBacs bacs))
        | ValueNone, ValueSome iban -> ValueSome(PayerIban iban)
        | ValueNone, ValueNone -> ValueNone
      Scheme = tryCode schemes "scheme" e
      ConsentId = Json.tryString "consent_id" e
      EndToEndId = Json.tryString "end_to_end_id" e
      RefundIds = Json.strings "refund_ids" e
      Error = Parse.embeddedError "error" e
      Raw = e.GetRawText() }

/// The payment's current state. In production prefer the PAYMENT_STATUS_UPDATE webhook to polling.
let getPaymentAsync (client: PlaidClient) (paymentId: string) ct =
    Api.postAsync client "/payment_initiation/payment/get" {| payment_id = paymentId |} payment ct

/// Payments created before cursor (ValueNone for the newest), at most count (1-200); returns the next cursor.
let listPaymentsAsync (client: PlaidClient) (count: int) (cursor: string voption) ct =
    let parse (e: JsonElement) =
        Json.array "payments" e |> List.map payment, Json.tryString "next_cursor" e

    Api.postAsync client "/payment_initiation/payment/list" {| count = count; cursor = cursor |} parse ct

/// Link token for the payer to authorise a payment or consent. Payment Initiation cannot share Link with
/// other products; no public token exchange follows.
let private paymentInitiationLinkToken client clientUserId (userId: string voption) (target: obj) ct =
    Api.createLinkTokenForProductsAsync
        client
        clientUserId
        [ "payment_initiation" ]
        [ "payment_initiation", target
          match userId with
          | ValueSome user -> "user_id", box user
          | ValueNone -> () ]
        ct

/// Link token for the payer to authorise paymentId; Link's onSuccess means the payment is authorising.
let createPaymentLinkTokenAsync (client: PlaidClient) (clientUserId: string) (userId: string voption) (paymentId: string) ct =
    paymentInitiationLinkToken client clientUserId userId {| payment_id = paymentId |} ct

/// Sandbox only: moves the payment to status and sends its webhook to webhookUrl. Returns the new status.
let sandboxSimulatePaymentAsync (client: PlaidClient) (paymentId: string) (status: PaymentStatus) (webhookUrl: string) ct =
    let body =
        {| payment_id = paymentId
           status = PaymentStatus.toString status
           webhook = webhookUrl |}

    Api.postAsync client "/sandbox/payment/simulate" body (Json.requiredCode "new_status" PaymentStatus.ofString) ct

// ---- Variable Recurring Payments (UK) ----

[<RequireQualifiedAccess>]
type ConsentStatus =
    | Unauthorised
    | Authorised
    | Rejected
    | Revoked
    | Expired
    | Other of string

module ConsentStatus =

    let private codes =
        Codes(
            [ ConsentStatus.Unauthorised, "UNAUTHORISED"
              ConsentStatus.Authorised, "AUTHORISED"
              ConsentStatus.Rejected, "REJECTED"
              ConsentStatus.Revoked, "REVOKED"
              ConsentStatus.Expired, "EXPIRED" ]
        )

    let ofString (code: string) =
        codes.TryValue code |> Option.defaultValue (ConsentStatus.Other code)

[<RequireQualifiedAccess>]
type ConsentType =
    /// Between the payer's own accounts.
    | Sweeping
    | Commercial

let private consentTypes =
    Codes([ ConsentType.Sweeping, "SWEEPING"; ConsentType.Commercial, "COMMERCIAL" ])

[<RequireQualifiedAccess>]
type Period =
    | Day
    | Week
    | Month
    | Year

let private periods =
    Codes([ Period.Day, "DAY"; Period.Week, "WEEK"; Period.Month, "MONTH"; Period.Year, "YEAR" ])

[<RequireQualifiedAccess>]
type PeriodAlignment =
    /// Periods follow the calendar; the first one is pro-rated.
    | Calendar
    /// Periods start when the consent does.
    | Consent

let private alignments =
    Codes([ PeriodAlignment.Calendar, "CALENDAR"; PeriodAlignment.Consent, "CONSENT" ])

type PeriodicAmount =
    { Amount: Money
      Interval: Period
      Alignment: PeriodAlignment }

type ConsentConstraints =
    { ValidFrom: DateTimeOffset voption
      ValidTo: DateTimeOffset voption
      MaxPaymentAmount: Money
      /// At least one.
      PeriodicAmounts: PeriodicAmount list }

/// Who pays, so the bank can check the consent is set up from the payer's own account.
type ConsentPayer = { Name: string; Account: PayerAccount }

type ConsentRequest =
    { RecipientId: string
      /// 1-18 letters, digits and spaces.
      Reference: string
      UserId: string voption
      Type: ConsentType
      Constraints: ConsentConstraints
      Payer: ConsentPayer voption }

/// Creates a VRP consent; the payer authorises it in Link (createConsentLinkTokenAsync). Store the
/// returned consent_id: Plaid cannot look it up later.
let createConsentAsync (client: PlaidClient) (request: ConsentRequest) ct =
    withReference request.Reference (fun () ->
        let constraints = request.Constraints

        let validDateTime =
            match constraints.ValidFrom, constraints.ValidTo with
            | ValueNone, ValueNone -> ValueNone
            | from, until ->
                ValueSome
                    {| from = from |> ValueOption.map Request.timestamp
                       ``to`` = until |> ValueOption.map Request.timestamp |}

        let body =
            {| recipient_id = request.RecipientId
               reference = request.Reference
               user_id = request.UserId
               ``type`` = consentTypes.Code request.Type
               constraints =
                {| valid_date_time = validDateTime
                   max_payment_amount = money constraints.MaxPaymentAmount
                   periodic_amounts =
                    constraints.PeriodicAmounts
                    |> List.map (fun p ->
                        {| amount = money p.Amount
                           interval = periods.Code p.Interval
                           alignment = alignments.Code p.Alignment |}) |}
               payer_details =
                request.Payer
                |> ValueOption.map (fun p ->
                    let iban, bacs = payerFields (ValueSome p.Account)

                    {| name = p.Name
                       account_numbers = {| iban = iban; bacs = bacs |} |}) |}

        let parse (e: JsonElement) =
            Json.requiredString "consent_id" e, Json.requiredCode "status" ConsentStatus.ofString e

        Api.postAsync client "/payment_initiation/consent/create" body parse ct)

type Consent =
    { ConsentId: string
      Status: ConsentStatus
      RecipientId: string voption
      Reference: string voption
      Type: ConsentType voption
      CreatedAt: DateTimeOffset voption
      /// Including the constraints and payer details.
      Raw: string }

let getConsentAsync (client: PlaidClient) (consentId: string) ct =
    let parse (e: JsonElement) : Consent =
        { ConsentId = Json.requiredString "consent_id" e
          Status = Json.requiredCode "status" ConsentStatus.ofString e
          RecipientId = Json.tryString "recipient_id" e
          Reference = Json.tryString "reference" e
          Type = tryCode consentTypes "type" e
          CreatedAt = Json.tryTimestamp "created_at" e
          Raw = e.GetRawText() }

    Api.postAsync client "/payment_initiation/consent/get" {| consent_id = consentId |} parse ct

let revokeConsentAsync (client: PlaidClient) (consentId: string) ct =
    Api.postAsync client "/payment_initiation/consent/revoke" {| consent_id = consentId |} ignore ct

/// Takes a payment (GBP) under an authorised consent, without the payer. idempotencyKey (1-128 chars)
/// makes a retry within 48 hours return the first attempt instead of paying twice.
let executeConsentPaymentAsync
    (client: PlaidClient)
    (consentId: string)
    (amount: Money)
    (idempotencyKey: string)
    (reference: string voption)
    ct
    =
    let call () =
        let body =
            {| consent_id = consentId
               amount = money amount
               idempotency_key = idempotencyKey
               reference = reference |}

        Api.postAsync client "/payment_initiation/consent/payment/execute" body paymentCreated ct

    match reference with
    | ValueSome r -> withReference r call
    | ValueNone -> call ()

/// Link token for the payer to authorise consentId.
let createConsentLinkTokenAsync (client: PlaidClient) (clientUserId: string) (userId: string voption) (consentId: string) ct =
    paymentInitiationLinkToken client clientUserId userId {| consent_id = consentId |} ct
