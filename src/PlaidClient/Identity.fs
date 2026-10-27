/// Identity checks: who owns a linked bank account (Identity, Identity Match) and who the customer is
/// (Identity Verification, Plaid's KYC). Together they cover the account-ownership and KYC checks that
/// payment and lending regulation asks for.
module Plaid.Identity

open System
open System.Text.Json

type AccountOwners = { Account: Account; Owners: Owner list }

type IdentityResult =
    { Item: ItemInfo
      Accounts: AccountOwners list }

let private identityResult (e: JsonElement) : IdentityResult =
    { Item = Parse.item e
      Accounts =
        Json.array "accounts" e
        |> List.map (fun a ->
            { Account = Parse.account a
              Owners = Json.array "owners" a |> List.map Parse.owner }) }

/// Account holders' names, emails, phone numbers and addresses as the bank reports them (the Identity
/// product). accountIds limits it to those accounts ([] for all).
let getAsync (client: PlaidClient) (accessToken: string) (accountIds: string list) ct =
    let body =
        {| access_token = accessToken
           options = Request.accountOptions accountIds |}

    Api.postAsync client "/identity/get" body identityResult ct

/// The customer as the application's records hold them, to compare with the account holder.
type MatchUser =
    { LegalName: string voption
      /// E.164, e.g. "+447700900123".
      PhoneNumber: string voption
      EmailAddress: string voption
      Address: PostalAddress voption }

module MatchUser =

    /// No details: Plaid then compares with the identity verified by Identity Verification, when
    /// Financial Account Matching is enabled for the account.
    let empty =
        { LegalName = ValueNone
          PhoneNumber = ValueNone
          EmailAddress = ValueNone
          Address = ValueNone }

    let ofName legalName = { empty with LegalName = ValueSome legalName }

[<Struct>]
type NameScore =
    { /// 0-100; ValueNone when the bank gave no name.
      Score: int voption
      /// Only the first or the last name matched: often a family member's account.
      IsFirstNameOrLastNameMatch: bool voption
      IsNicknameMatch: bool voption
      IsBusinessNameDetected: bool voption }

[<Struct>]
type AddressScore =
    { Score: int voption
      IsPostalCodeMatch: bool voption }

/// Scores of one account; each is ValueNone when either side lacks the data.
type AccountMatch =
    { Account: Account
      LegalName: NameScore voption
      PhoneNumber: int voption
      EmailAddress: int voption
      Address: AddressScore voption }

type MatchResult =
    { Item: ItemInfo
      Accounts: AccountMatch list }

/// Plaid's recommended minimum score for a field to count as a match.
[<Literal>]
let RecommendedThreshold = 70

let private score name (e: JsonElement) =
    Json.tryObject name e |> ValueOption.bind (Json.tryInt "score")

let private accountMatch (a: JsonElement) : AccountMatch =
    { Account = Parse.account a
      LegalName =
        Json.tryObject "legal_name" a
        |> ValueOption.map (fun n ->
            { Score = Json.tryInt "score" n
              IsFirstNameOrLastNameMatch = Json.tryBool "is_first_name_or_last_name_match" n
              IsNicknameMatch = Json.tryBool "is_nickname_match" n
              IsBusinessNameDetected = Json.tryBool "is_business_name_detected" n })
      PhoneNumber = score "phone_number" a
      EmailAddress = score "email_address" a
      Address =
        Json.tryObject "address" a
        |> ValueOption.map (fun ad ->
            { Score = Json.tryInt "score" ad
              IsPostalCodeMatch = Json.tryBool "is_postal_code_match" ad }) }

/// Compares user with each account's holders (Identity Match). Plaid scores every field 0-100.
let matchAsync (client: PlaidClient) (accessToken: string) (user: MatchUser) (accountIds: string list) ct =
    let body =
        {| access_token = accessToken
           user =
            {| legal_name = user.LegalName
               phone_number = user.PhoneNumber
               email_address = user.EmailAddress
               address =
                user.Address
                |> ValueOption.map (fun a ->
                    {| street = a.Street
                       city = a.City
                       region = a.Region
                       postal_code = a.PostalCode
                       country = a.Country |}) |}
           options = Request.accountOptions accountIds |}

    let parse (e: JsonElement) : MatchResult =
        { Item = Parse.item e
          Accounts = Json.array "accounts" e |> List.map accountMatch }

    Api.postAsync client "/identity/match" body parse ct

/// The legal name scored at least threshold. Plaid treats 70 and up as a match, 85 and up as strong.
let nameMatches (threshold: int) (account: AccountMatch) =
    account.LegalName
    |> ValueOption.bind _.Score
    |> ValueOption.exists (fun s -> s >= threshold)

// ---- Identity Verification (KYC) ----

[<RequireQualifiedAccess>]
type VerificationStatus =
    /// The user has not finished the flow.
    | Active
    | Success
    | Failed
    | Expired
    | Canceled
    /// Waiting for a manual review in the Plaid Dashboard.
    | PendingReview
    | Other of string

module VerificationStatus =

    let ofString =
        function
        | "active" -> VerificationStatus.Active
        | "success" -> VerificationStatus.Success
        | "failed" -> VerificationStatus.Failed
        | "expired" -> VerificationStatus.Expired
        | "canceled" -> VerificationStatus.Canceled
        | "pending_review" -> VerificationStatus.PendingReview
        | other -> VerificationStatus.Other other

[<RequireQualifiedAccess>]
type StepStatus =
    | Success
    | Active
    | Failed
    | WaitingForPrerequisite
    | NotApplicable
    | Skipped
    | Expired
    | Canceled
    | PendingReview
    | ManuallyApproved
    | ManuallyRejected
    | Other of string

module StepStatus =

    let ofString =
        function
        | "success" -> StepStatus.Success
        | "active" -> StepStatus.Active
        | "failed" -> StepStatus.Failed
        | "waiting_for_prerequisite" -> StepStatus.WaitingForPrerequisite
        | "not_applicable" -> StepStatus.NotApplicable
        | "skipped" -> StepStatus.Skipped
        | "expired" -> StepStatus.Expired
        | "canceled" -> StepStatus.Canceled
        | "pending_review" -> StepStatus.PendingReview
        | "manually_approved" -> StepStatus.ManuallyApproved
        | "manually_rejected" -> StepStatus.ManuallyRejected
        | other -> StepStatus.Other other

/// What an IDENTITY_VERIFICATION webhook reports.
[<RequireQualifiedAccess>]
type VerificationChange =
    /// The verification's status changed.
    | StatusUpdated
    /// The user finished a step.
    | StepUpdated
    /// The verification was retried.
    | Retried
    | Other of code: string

type Verification =
    { Id: string
      ClientUserId: string voption
      Status: VerificationStatus
      /// Each step of the template (accept_tos, verify_sms, kyc_check, documentary_verification,
      /// selfie_check, watchlist_screening, risk_check) with its status.
      Steps: Map<string, StepStatus>
      TemplateId: string voption
      CreatedAt: DateTimeOffset voption
      CompletedAt: DateTimeOffset voption
      PreviousAttemptId: string voption
      ShareableUrl: string voption
      /// Links to the Monitor watchlist screening (AML/PEP), when the template includes it.
      WatchlistScreeningId: string voption
      /// The whole verification as Plaid sent it: document, selfie, KYC and risk details.
      Raw: string }

let private verification (e: JsonElement) : Verification =
    { Id = Json.requiredString "id" e
      ClientUserId = Json.tryString "client_user_id" e
      Status = Json.requiredString "status" e |> VerificationStatus.ofString
      Steps =
        match Json.tryObject "steps" e with
        | ValueSome steps ->
            steps.EnumerateObject()
            |> Seq.choose (fun p ->
                if p.Value.ValueKind = JsonValueKind.String then
                    Some(p.Name, StepStatus.ofString (p.Value.GetString()))
                else
                    None)
            |> Map.ofSeq
        | ValueNone -> Map.empty
      TemplateId = Json.tryObject "template" e |> ValueOption.bind (Json.tryString "id")
      CreatedAt = Json.tryTimestamp "created_at" e
      CompletedAt = Json.tryTimestamp "completed_at" e
      PreviousAttemptId = Json.tryString "previous_attempt_id" e
      ShareableUrl = Json.tryString "shareable_url" e
      WatchlistScreeningId = Json.tryString "watchlist_screening_id" e
      Raw = e.GetRawText() }

type VerificationAddress =
    { Street: string voption
      Street2: string voption
      City: string voption
      /// ISO 3166-2 subdivision code.
      Region: string voption
      PostalCode: string voption
      /// ISO 3166-1 alpha-2.
      Country: string }

type PersonName = { GivenName: string; FamilyName: string }

/// Details already collected, so Link can skip asking for them.
type VerificationUser =
    { EmailAddress: string voption
      PhoneNumber: string voption
      DateOfBirth: DateTime voption
      Name: PersonName voption
      Address: VerificationAddress voption }

module VerificationUser =

    let empty =
        { EmailAddress = ValueNone
          PhoneNumber = ValueNone
          DateOfBirth = ValueNone
          Name = ValueNone
          Address = ValueNone }

type VerificationRequest =
    { /// The Identity Verification template configured in the Plaid Dashboard.
      TemplateId: string
      ClientUserId: string
      User: VerificationUser
      /// The user already accepted the terms in your app, so Link skips that step.
      GaveConsent: bool
      /// Plaid may create a shareable link for the verification.
      IsShareable: bool
      /// Return the user's existing verification for the template instead of creating another.
      IsIdempotent: bool }

module VerificationRequest =

    let create templateId clientUserId =
        { TemplateId = templateId
          ClientUserId = clientUserId
          User = VerificationUser.empty
          GaveConsent = false
          IsShareable = false
          IsIdempotent = true }

let private verificationUser (user: VerificationUser) =
    {| email_address = user.EmailAddress
       phone_number = user.PhoneNumber
       date_of_birth = user.DateOfBirth |> ValueOption.map Request.isoDate
       name =
        user.Name
        |> ValueOption.map (fun n ->
            {| given_name = n.GivenName
               family_name = n.FamilyName |})
       address =
        user.Address
        |> ValueOption.map (fun a ->
            {| street = a.Street
               street2 = a.Street2
               city = a.City
               region = a.Region
               postal_code = a.PostalCode
               country = a.Country |}) |}

/// Starts (or with IsIdempotent returns) a verification without Link, e.g. to prefill data before
/// createVerificationLinkTokenAsync.
let createVerificationAsync (client: PlaidClient) (request: VerificationRequest) ct =
    let body =
        {| template_id = request.TemplateId
           client_user_id = request.ClientUserId
           user = verificationUser request.User
           gave_consent = request.GaveConsent
           is_shareable = request.IsShareable
           is_idempotent = request.IsIdempotent |}

    Api.postAsync client "/identity_verification/create" body verification ct

/// The verification's status, steps and details. Link's onSuccess only means the user finished the
/// flow, so read the outcome here (after the STATUS_UPDATED webhook).
let getVerificationAsync (client: PlaidClient) (verificationId: string) ct =
    Api.postAsync client "/identity_verification/get" {| identity_verification_id = verificationId |} verification ct

[<RequireQualifiedAccess>]
type RetryStrategy =
    /// Start again from the first step.
    | Reset
    /// Resume at the failed step; not allowed when watchlist screening or risk check failed.
    | Incomplete
    /// Plaid picks Reset or Incomplete; not allowed while the verification is active.
    | Infer

/// Gives the user another attempt.
let retryVerificationAsync (client: PlaidClient) (clientUserId: string) (templateId: string) (strategy: RetryStrategy) ct =
    let strategyName =
        match strategy with
        | RetryStrategy.Reset -> "reset"
        | RetryStrategy.Incomplete -> "incomplete"
        | RetryStrategy.Infer -> "infer"

    let body =
        {| client_user_id = clientUserId
           template_id = templateId
           strategy = strategyName |}

    Api.postAsync client "/identity_verification/retry" body verification ct

/// Link token for the Identity Verification flow; it cannot be combined with other products.
/// clientUserId must be the one the verification was (or will be) created with.
let createVerificationLinkTokenAsync
    (client: PlaidClient)
    (clientUserId: string)
    (templateId: string)
    (emailAddress: string voption)
    ct
    =
    Api.createLinkTokenForProductsAsync
        client
        clientUserId
        [ "identity_verification" ]
        [ "identity_verification", box {| template_id = templateId |}
          "user",
          box
              {| client_user_id = clientUserId
                 email_address = emailAddress |} ]
        ct
