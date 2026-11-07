/// Monitor's entity watchlist screening: a business checked against the lists of an entity program, with the
/// hits and reviews that follow. Individual screening has no typed calls here: use Api.postAsync.
///
/// A screening's status is the customer's own decision state, not a finding of Plaid's: see ScreeningStatus and
/// EntityScreeningOutcome.
module Plaid.Monitor

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// A screening's state. Plaid's webhook reports a change to it without saying what it became.
[<RequireQualifiedAccess>]
type ScreeningStatus =
    /// No single meaning: the name was on none of the program's lists when screened, or a reviewer dismissed its
    /// hit, or the customer's own code set it with updateEntityScreeningAsync, which asks for no review. AuditTrail
    /// tells them apart only as far as its Source reaches.
    | Cleared
    /// The screening has hits to review, or was marked for review again after being cleared or rejected.
    | PendingReview
    /// A reviewer confirmed a hit as a valid match. A rejected screening gets no new hits, whatever its program's
    /// settings.
    | Rejected
    /// A status this client does not know yet.
    | Other of string

module ScreeningStatus =

    let private codes =
        Payments.Codes(
            [ ScreeningStatus.Cleared, "cleared"
              ScreeningStatus.PendingReview, "pending_review"
              ScreeningStatus.Rejected, "rejected" ]
        )

    let ofString (code: string) =
        codes.TryValue code |> Option.defaultValue (ScreeningStatus.Other code)

    let toString =
        function
        | ScreeningStatus.Other code -> code
        | status -> codes.Code status

/// A hit's state: not the same words as ScreeningStatus. Every hit starts as PendingReview, follows the latest list
/// data and search terms until reviewed, and stays as it was when reviewed afterwards.
[<RequireQualifiedAccess>]
type HitStatus =
    | PendingReview
    /// A reviewer marked it a true positive.
    | Confirmed
    /// A reviewer marked it a false positive.
    | Dismissed
    /// A status this client does not know yet.
    | Other of string

module HitStatus =

    let private codes =
        Payments.Codes(
            [ HitStatus.PendingReview, "pending_review"
              HitStatus.Confirmed, "confirmed"
              HitStatus.Dismissed, "dismissed" ]
        )

    let ofString (code: string) =
        codes.TryValue code |> Option.defaultValue (HitStatus.Other code)

    let toString =
        function
        | HitStatus.Other code -> code
        | status -> codes.Code status

/// Who or what last touched an object.
[<RequireQualifiedAccess>]
type AuditSource =
    /// A person in the Plaid Dashboard; AuditTrail.DashboardUserId names them.
    | Dashboard
    | Link
    /// A call with the customer's credentials: its own code, or a rule of its own.
    | Api
    /// Plaid.
    | System
    /// A screening created in a bulk screening creation.
    | Retro
    /// A source this client does not know yet.
    | Other of string

module AuditSource =

    let private codes =
        Payments.Codes(
            [ AuditSource.Dashboard, "dashboard"
              AuditSource.Link, "link"
              AuditSource.Api, "api"
              AuditSource.System, "system"
              AuditSource.Retro, "retro" ]
        )

    let ofString (code: string) =
        codes.TryValue code |> Option.defaultValue (AuditSource.Other code)

    let toString =
        function
        | AuditSource.Other code -> code
        | source -> codes.Code source

/// The last change to an object. It covers any change, such as an edit to the search terms or the assignee, so it need
/// not be the change that set the status: listEntityHistoryAsync holds the earlier ones.
type AuditTrail =
    { Source: AuditSource
      /// Look the person up with /dashboard_user/get.
      DashboardUserId: string voption
      Timestamp: DateTimeOffset }

/// A list an entity program can screen against.
[<RequireQualifiedAccess>]
type EntityListCode =
    /// AU_CON: Australia Department of Foreign Affairs and Trade Consolidated List
    | AuCon
    /// CA_CON: Government of Canada Consolidated List of Sanctions
    | CaCon
    /// EU_CON: European External Action Service Consolidated List
    | EuCon
    /// IZ_SOE: State Owned Enterprise List
    | IzSoe
    /// IZ_UNC: United Nations Consolidated Sanctions
    | IzUnc
    /// IZ_WBK: World Bank Listing of Ineligible Firms and Individuals
    | IzWbk
    /// UK_HMC: Foreign, Commonwealth and Development Office UK Sanctions List
    | UkHmc
    /// US_CAP: US OFAC Correspondent Account or Payable-Through Account Sanctions
    | UsCap
    /// US_CMC: US OFAC Non-SDN Chinese Military-Industrial Complex List
    | UsCmc
    /// US_FSE: US OFAC Foreign Sanctions Evaders
    | UsFse
    /// US_MBS: US Non-SDN Menu-Based Sanctions
    | UsMbs
    /// US_SAM: US System for Award Management Exclusion List
    | UsSam
    /// US_SDN: US OFAC Specially Designated Nationals List
    | UsSdn
    /// US_SSI: US OFAC Sectoral Sanctions Identifications
    | UsSsi
    /// US_TEL: US Terrorist Exclusion List
    | UsTel
    /// US_UVL: Bureau of Industry and Security Unverified List
    | UsUvl
    /// A list this client does not know yet.
    | Other of string

module EntityListCode =

    let private codes =
        Payments.Codes(
            [ EntityListCode.AuCon, "AU_CON"
              EntityListCode.CaCon, "CA_CON"
              EntityListCode.EuCon, "EU_CON"
              EntityListCode.IzSoe, "IZ_SOE"
              EntityListCode.IzUnc, "IZ_UNC"
              EntityListCode.IzWbk, "IZ_WBK"
              EntityListCode.UkHmc, "UK_HMC"
              EntityListCode.UsCap, "US_CAP"
              EntityListCode.UsCmc, "US_CMC"
              EntityListCode.UsFse, "US_FSE"
              EntityListCode.UsMbs, "US_MBS"
              EntityListCode.UsSam, "US_SAM"
              EntityListCode.UsSdn, "US_SDN"
              EntityListCode.UsSsi, "US_SSI"
              EntityListCode.UsTel, "US_TEL"
              EntityListCode.UsUvl, "US_UVL" ]
        )

    let ofString (code: string) =
        codes.TryValue code |> Option.defaultValue (EntityListCode.Other code)

    let toString =
        function
        | EntityListCode.Other code -> code
        | list -> codes.Code list

/// How a field of a hit compares with the search terms.
[<RequireQualifiedAccess>]
type MatchSummary =
    /// A strong match.
    | Match
    | PartialMatch
    | NoMatch
    /// Plaid found nothing external to compare with.
    | NoData
    /// The search terms hold nothing for this field.
    | NoInput
    /// A value this client does not know yet.
    | Other of string

module MatchSummary =

    let private codes =
        Payments.Codes(
            [ MatchSummary.Match, "match"
              MatchSummary.PartialMatch, "partial_match"
              MatchSummary.NoMatch, "no_match"
              MatchSummary.NoData, "no_data"
              MatchSummary.NoInput, "no_input" ]
        )

    let ofString (code: string) =
        codes.TryValue code |> Option.defaultValue (MatchSummary.Other code)

/// A program's name matching setting.
[<RequireQualifiedAccess>]
type NameSensitivity =
    /// More potential matches, more noise.
    | Coarse
    | Balanced
    /// Fewer false positives; names must be more similar.
    | Strict
    /// Nearly exact names only.
    | Exact
    /// A setting this client does not know yet.
    | Other of string

module NameSensitivity =

    let private codes =
        Payments.Codes(
            [ NameSensitivity.Coarse, "coarse"
              NameSensitivity.Balanced, "balanced"
              NameSensitivity.Strict, "strict"
              NameSensitivity.Exact, "exact" ]
        )

    let ofString (code: string) =
        codes.TryValue code |> Option.defaultValue (NameSensitivity.Other code)

/// What a screening searches for.
type EntitySearchTerms =
    { /// The entity program, entprg_...
      ProgramId: string
      /// At least one letter, at most 100 characters, no leading or trailing spaces.
      LegalName: string
      /// 4-32 characters.
      DocumentNumber: string voption
      EmailAddress: string voption
      /// ISO 3166-1 alpha-2, capitalized.
      Country: string voption
      /// E.164.
      PhoneNumber: string voption
      /// Begins with http or https.
      Url: string voption }

module EntitySearchTerms =

    let create programId legalName =
        { ProgramId = programId
          LegalName = legalName
          DocumentNumber = ValueNone
          EmailAddress = ValueNone
          Country = ValueNone
          PhoneNumber = ValueNone
          Url = ValueNone }

type EntityScreening =
    { /// entscr_...
      Id: string
      SearchTerms: EntitySearchTerms
      /// Starts at 1 and rises with each edit to the search terms.
      SearchTermsVersion: int voption
      /// The Dashboard user the review is assigned to.
      Assignee: string voption
      Status: ScreeningStatus
      ClientUserId: string voption
      AuditTrail: AuditTrail
      /// The screening exactly as Plaid sent it.
      Raw: string }

type HitAnalysis =
    { Names: MatchSummary voption
      Documents: MatchSummary voption
      Locations: MatchSummary voption
      EmailAddresses: MatchSummary voption
      PhoneNumbers: MatchSummary voption
      Urls: MatchSummary voption
      /// The version of the search terms the hit was compared with.
      SearchTermsVersion: int voption }

type HitName =
    { Full: string
      /// At most one name of a hit is primary.
      IsPrimary: bool
      Summary: MatchSummary voption }

type EntityHit =
    { /// enthit_...
      Id: string
      ReviewStatus: HitStatus
      ListCode: EntityListCode
      /// Plaid's identifier for the listed party, stable across searches and updates.
      PlaidUid: string voption
      /// The list's own identifier, where it gives one.
      SourceUid: string voption
      /// Designations from the issuing authority, e.g. SDGT; new ones arrive without notice, so they stay strings.
      SubPrograms: string list
      FirstActive: DateTimeOffset voption
      InactiveSince: DateTimeOffset voption
      HistoricalSince: DateTimeOffset voption
      Analysis: HitAnalysis voption
      /// The listed party's names. Its documents, addresses, emails, phone numbers and URLs are in Raw.
      Names: HitName list
      /// The hit exactly as Plaid sent it.
      Raw: string }

/// A hit of a page that could not be read: its id, review_status or list_code is missing. Printing one (%A, ToString)
/// hides Raw.
[<StructuredFormatDisplay("{Display}")>]
type UnparsedHit =
    { Reason: string
      /// The entry exactly as Plaid sent it. It holds the search terms it matched: keep it out of logs.
      Raw: string }

    member this.Display = $"UnparsedHit {{ Reason = {this.Reason}; Raw = *** }}"
    override this.ToString() = this.Display

/// Every hit of a screening.
type EntityHitList =
    { Hits: EntityHit list
      /// Entries that could not be read. Each keeps EntityScreeningOutcome from reading clear.
      Unparsed: UnparsedHit list }

/// A review by a member of the customer's team. It holds actions on hits, a comment, or both.
type EntityReview =
    { /// entrev_...
      Id: string
      ConfirmedHits: string list
      DismissedHits: string list
      Comment: string voption
      AuditTrail: AuditTrail
      /// The review exactly as Plaid sent it.
      Raw: string }

/// An entity program: the lists and settings a screening runs under. A screening says nothing about a list that isn't in
/// ListsEnabled.
type EntityProgram =
    { /// entprg_...
      Id: string
      Name: string
      CreatedAt: DateTimeOffset
      /// The program rescans daily, so its screenings can go back to review.
      IsRescanningEnabled: bool
      ListsEnabled: EntityListCode list
      NameSensitivity: NameSensitivity
      /// Read-only: it screens no new customers and takes no part in ongoing monitoring.
      IsArchived: bool
      AuditTrail: AuditTrail
      /// The program exactly as Plaid sent it.
      Raw: string }

/// Search terms to change on a screening. Plaid requires the program even when only another field changes, and moves the
/// screening when it differs.
type EntitySearchTermsUpdate =
    { ProgramId: string
      LegalName: string voption
      DocumentNumber: string voption
      EmailAddress: string voption
      Country: string voption
      PhoneNumber: string voption
      Url: string voption }

module EntitySearchTermsUpdate =

    let create programId : EntitySearchTermsUpdate =
        { ProgramId = programId
          LegalName = ValueNone
          DocumentNumber = ValueNone
          EmailAddress = ValueNone
          Country = ValueNone
          PhoneNumber = ValueNone
          Url = ValueNone }

/// What to change on a screening; ValueNone leaves a field as it is.
type EntityScreeningUpdate =
    { ScreeningId: string
      SearchTerms: EntitySearchTermsUpdate voption
      Assignee: string voption
      /// Plaid's spec asks for no review before ScreeningStatus.Cleared is set here.
      Status: ScreeningStatus voption
      ClientUserId: string voption
      /// Sets the assignee back to none.
      ResetAssignee: bool }

module EntityScreeningUpdate =

    let create screeningId =
        { ScreeningId = screeningId
          SearchTerms = ValueNone
          Assignee = ValueNone
          Status = ValueNone
          ClientUserId = ValueNone
          ResetAssignee = false }

/// Which screenings of a program to list; ValueNone doesn't filter on that field.
type EntityScreeningFilter =
    { ClientUserId: string voption
      Status: ScreeningStatus voption
      Assignee: string voption }

module EntityScreeningFilter =

    let all =
        { ClientUserId = ValueNone
          Status = ValueNone
          Assignee = ValueNone }

let private auditTrail (e: JsonElement) : AuditTrail =
    let trail = Json.required "audit_trail" Json.tryObject e

    { Source = Json.requiredCode "source" AuditSource.ofString trail
      DashboardUserId = Json.tryString "dashboard_user_id" trail
      Timestamp = Json.required "timestamp" Json.tryTimestamp trail }

let private searchTerms (e: JsonElement) : EntitySearchTerms =
    { ProgramId = Json.requiredString "entity_watchlist_program_id" e
      LegalName = Json.requiredString "legal_name" e
      DocumentNumber = Json.tryString "document_number" e
      EmailAddress = Json.tryString "email_address" e
      Country = Json.tryString "country" e
      PhoneNumber = Json.tryString "phone_number" e
      Url = Json.tryString "url" e }

let private screening (e: JsonElement) : EntityScreening =
    let terms = Json.required "search_terms" Json.tryObject e

    { Id = Json.requiredString "id" e
      SearchTerms = searchTerms terms
      SearchTermsVersion = Json.tryInt "version" terms
      Assignee = Json.tryString "assignee" e
      Status = Json.requiredCode "status" ScreeningStatus.ofString e
      ClientUserId = Json.tryString "client_user_id" e
      AuditTrail = auditTrail e
      Raw = e.GetRawText() }

let private matchSummary name (e: JsonElement) =
    Json.tryString name e |> ValueOption.map MatchSummary.ofString

let private hitAnalysis (e: JsonElement) : HitAnalysis =
    { Names = matchSummary "names" e
      Documents = matchSummary "documents" e
      Locations = matchSummary "locations" e
      EmailAddresses = matchSummary "email_addresses" e
      PhoneNumbers = matchSummary "phone_numbers" e
      Urls = matchSummary "urls" e
      SearchTermsVersion = Json.tryInt "search_terms_version" e }

/// One entry of data.names: {"analysis":{"summary":...},"data":{"full":...,"is_primary":...}}.
let private hitName (e: JsonElement) : HitName voption =
    Json.tryObject "data" e
    |> ValueOption.bind (fun name ->
        Json.tryString "full" name
        |> ValueOption.map (fun full ->
            { Full = full
              IsPrimary = Json.tryBool "is_primary" name |> ValueOption.defaultValue false
              Summary = Json.tryObject "analysis" e |> ValueOption.bind (matchSummary "summary") }))

let private hit (e: JsonElement) : EntityHit =
    { Id = Json.requiredString "id" e
      ReviewStatus = Json.requiredCode "review_status" HitStatus.ofString e
      ListCode = Json.requiredCode "list_code" EntityListCode.ofString e
      PlaidUid = Json.tryString "plaid_uid" e
      SourceUid = Json.tryString "source_uid" e
      SubPrograms = Json.strings "sub_programs" e
      FirstActive = Json.tryTimestamp "first_active" e
      InactiveSince = Json.tryTimestamp "inactive_since" e
      HistoricalSince = Json.tryTimestamp "historical_since" e
      Analysis = Json.tryObject "analysis" e |> ValueOption.map hitAnalysis
      Names =
        Json.tryObject "data" e
        |> ValueOption.map (Json.array "names" >> List.choose (hitName >> Json.toOption))
        |> ValueOption.defaultValue []
      Raw = e.GetRawText() }

let private hitIds name (e: JsonElement) =
    Json.requiredArray name e
    |> List.map (fun id ->
        if id.ValueKind = JsonValueKind.String then
            id.GetString()
        else
            raise (Json.InvalidJson $"Invalid entry in '{name}'"))

let private review (e: JsonElement) : EntityReview =
    { Id = Json.requiredString "id" e
      ConfirmedHits = hitIds "confirmed_hits" e
      DismissedHits = hitIds "dismissed_hits" e
      Comment = Json.tryString "comment" e
      AuditTrail = auditTrail e
      Raw = e.GetRawText() }

let private program (e: JsonElement) : EntityProgram =
    { Id = Json.requiredString "id" e
      Name = Json.requiredString "name" e
      CreatedAt = Json.required "created_at" Json.tryTimestamp e
      IsRescanningEnabled = Json.required "is_rescanning_enabled" Json.tryBool e
      ListsEnabled =
        Json.requiredArray "lists_enabled" e
        |> List.map (fun code ->
            if code.ValueKind = JsonValueKind.String then
                EntityListCode.ofString (code.GetString())
            else
                raise (Json.InvalidJson "Invalid entry in 'lists_enabled'"))
      NameSensitivity = Json.requiredCode "name_sensitivity" NameSensitivity.ofString e
      IsArchived = Json.required "is_archived" Json.tryBool e
      AuditTrail = auditTrail e
      Raw = e.GetRawText() }

/// One page: its entries, and the next cursor. A page without its array or without next_cursor can't be read: it could
/// otherwise end a list that goes on. A null or empty next_cursor ends the list.
let private page (name: string) (read: JsonElement -> 'T) (e: JsonElement) =
    let entries = Json.requiredArray name e |> List.map read
    entries, Json.requiredNullableString "next_cursor" e |> ValueOption.filter (String.IsNullOrEmpty >> not)

/// Follows next_cursor to the last page. Any page that fails, or a cursor Plaid already gave, fails the whole list, so a
/// caller never holds part of a list as if it were all of it.
let private allPages
    (client: PlaidClient)
    (path: string)
    (body: string voption -> obj)
    (name: string)
    (read: JsonElement -> 'T)
    (ct: CancellationToken)
    : Task<Result<'T list, PlaidFailure>> =
    let rec loop cursor (seen: Set<string>) (pages: 'T list list) =
        task {
            match! Api.postAsync client path (body cursor) (page name read) ct with
            | Error e -> return Error e
            | Ok(entries, ValueNone) -> return Ok(List.rev (entries :: pages) |> List.concat)
            | Ok(_, ValueSome next) when seen.Contains next ->
                return Error(InvalidResponse $"{path} repeated next_cursor '{next}'")
            | Ok(entries, ValueSome next) -> return! loop (ValueSome next) (seen.Add next) (entries :: pages)
        }

    loop ValueNone Set.empty []

/// Screens a business against its program's lists. With rescanning on, these terms are what Plaid monitors over time.
/// clientUserId is the application's stable, non-identifying id for the business.
let createEntityScreeningAsync (client: PlaidClient) (terms: EntitySearchTerms) (clientUserId: string) ct =
    let body =
        {| search_terms =
            {| entity_watchlist_program_id = terms.ProgramId
               legal_name = terms.LegalName
               document_number = terms.DocumentNumber
               email_address = terms.EmailAddress
               country = terms.Country
               phone_number = terms.PhoneNumber
               url = terms.Url |}
           client_user_id = clientUserId |}

    Api.postAsync client "/watchlist_screening/entity/create" body screening ct

/// One screening: read it after the ENTITY_SCREENING STATUS_UPDATED webhook, which carries no status.
let getEntityScreeningAsync (client: PlaidClient) (screeningId: string) ct =
    Api.postAsync client "/watchlist_screening/entity/get" {| entity_watchlist_screening_id = screeningId |} screening ct

/// Every screening of a program that passes filter, across all pages.
let listEntityScreeningsAsync (client: PlaidClient) (programId: string) (filter: EntityScreeningFilter) ct =
    let body (cursor: string voption) =
        box
            {| entity_watchlist_program_id = programId
               client_user_id = filter.ClientUserId
               status = filter.Status |> ValueOption.map ScreeningStatus.toString
               assignee = filter.Assignee
               cursor = cursor |}

    allPages client "/watchlist_screening/entity/list" body "entity_watchlist_screenings" screening ct

/// Changes a screening's search terms, assignee, status or client user id.
let updateEntityScreeningAsync (client: PlaidClient) (update: EntityScreeningUpdate) ct =
    let body =
        {| entity_watchlist_screening_id = update.ScreeningId
           search_terms =
            update.SearchTerms
            |> ValueOption.map (fun terms ->
                {| entity_watchlist_program_id = terms.ProgramId
                   legal_name = terms.LegalName
                   document_number = terms.DocumentNumber
                   email_address = terms.EmailAddress
                   country = terms.Country
                   phone_number = terms.PhoneNumber
                   url = terms.Url |})
           assignee = update.Assignee
           status = update.Status |> ValueOption.map ScreeningStatus.toString
           client_user_id = update.ClientUserId
           reset_fields = if update.ResetAssignee then [ "assignee" ] else [] |}

    Api.postAsync client "/watchlist_screening/entity/update" body screening ct

/// Every hit of a screening, across all pages. A hit that can't be read is in Unparsed; a page that can't be read fails
/// the whole list.
let listEntityHitsAsync (client: PlaidClient) (screeningId: string) ct =
    let body (cursor: string voption) =
        box
            {| entity_watchlist_screening_id = screeningId
               cursor = cursor |}

    let read (e: JsonElement) =
        try
            Ok(hit e)
        with Json.InvalidJson reason ->
            Error { Reason = reason; Raw = e.GetRawText() }

    task {
        match! allPages client "/watchlist_screening/entity/hit/list" body "entity_watchlist_screening_hits" read ct with
        | Error e -> return Error e
        | Ok entries ->
            return
                Ok
                    { Hits = entries |> List.choose (function Ok found -> Some found | Error _ -> None)
                      Unparsed = entries |> List.choose (function Error unparsed -> Some unparsed | Ok _ -> None) }
    }

/// Records a review: hits to confirm, hits to dismiss, and a comment, each optional. Confirmed and dismissed hits are
/// final. Plaid's docs don't say a review changes the screening's status: read it again afterwards, and set it with
/// updateEntityScreeningAsync where it hasn't changed.
let createEntityReviewAsync
    (client: PlaidClient)
    (screeningId: string)
    (confirmedHits: string list)
    (dismissedHits: string list)
    (comment: string voption)
    ct
    =
    // Plaid requires both lists even when empty, and request bodies leave out empty F# lists.
    let body =
        {| entity_watchlist_screening_id = screeningId
           confirmed_hits = Array.ofList confirmedHits
           dismissed_hits = Array.ofList dismissedHits
           comment = comment |}

    Api.postAsync client "/watchlist_screening/entity/review/create" body review ct

/// Every review of a screening, across all pages.
let listEntityReviewsAsync (client: PlaidClient) (screeningId: string) ct =
    let body (cursor: string voption) =
        box
            {| entity_watchlist_screening_id = screeningId
               cursor = cursor |}

    allPages client "/watchlist_screening/entity/review/list" body "entity_watchlist_screening_reviews" review ct

/// Every change to a screening, newest first, across all pages. Empty for a screening never edited.
let listEntityHistoryAsync (client: PlaidClient) (screeningId: string) ct =
    let body (cursor: string voption) =
        box
            {| entity_watchlist_screening_id = screeningId
               cursor = cursor |}

    allPages client "/watchlist_screening/entity/history/list" body "entity_watchlist_screenings" screening ct

/// One program, with the lists it screens against.
let getEntityProgramAsync (client: PlaidClient) (programId: string) ct =
    Api.postAsync client "/watchlist_screening/entity/program/get" {| entity_watchlist_program_id = programId |} program ct

/// Every entity program, across all pages.
let listEntityProgramsAsync (client: PlaidClient) ct =
    let body (cursor: string voption) = box {| cursor = cursor |}
    allPages client "/watchlist_screening/entity/program/list" body "entity_watchlist_programs" program ct

/// Why a screening can't be read as clear, besides the hits themselves.
[<RequireQualifiedAccess>]
type ScreeningGap =
    /// Plaid's status is pending_review or rejected.
    | StatusNotCleared of ScreeningStatus
    /// A status Plaid's spec doesn't list.
    | UnknownStatus of status: string
    /// A hit status Plaid's spec doesn't list. The hit is among the hits to look at.
    | UnknownHitStatus of hitId: string * status: string
    /// A hit that could not be read.
    | UnreadableHit of UnparsedHit
    /// A page of the hit list failed, so nothing is known of the hits.
    | HitListFailed of PlaidFailure
    /// The screening itself could not be read, so nothing is known of its status.
    | ScreeningReadFailed of PlaidFailure

/// What a Clear rests on. It is Plaid's status for this customer's program on this date: it says nothing of a list outside
/// the program, and a rescan or a person can change it.
type EntityScreeningClear =
    { ScreeningId: string
      ProgramId: string
      /// The name that was screened.
      LegalName: string
      /// The screening's last change: who or what made it, and when.
      AuditTrail: AuditTrail
      /// Hits a reviewer on the customer's team dismissed. The Clear rests on that decision; empty when none were found.
      DismissedHits: EntityHit list }

/// A neutral read of a screening and its hits. A 200 alone shows nothing: status is the customer's own decision state.
[<RequireQualifiedAccess>]
type EntityScreeningOutcome =
    /// Plaid's status is cleared and every hit read is dismissed.
    | Clear of EntityScreeningClear
    /// A person has to look: the status is pending_review or rejected, or a hit isn't dismissed, whatever the status says.
    /// Hits are those not dismissed. Gaps are the status and what also went unread.
    | NeedsReview of hits: EntityHit list * gaps: ScreeningGap list
    /// No hit was seen, and the status or the hits are unknown or could not be read. Never to be read as clear.
    | NotScreened of gaps: ScreeningGap list

module EntityScreeningOutcome =

    /// The outcome of what getEntityScreeningAsync and listEntityHitsAsync answered for one screening.
    let ofResults (screening: Result<EntityScreening, PlaidFailure>) (hits: Result<EntityHitList, PlaidFailure>) =
        let statusGaps, reviewAsked =
            match screening with
            | Error failure -> [ ScreeningGap.ScreeningReadFailed failure ], false
            | Ok read ->
                match read.Status with
                | ScreeningStatus.Cleared -> [], false
                | ScreeningStatus.PendingReview
                | ScreeningStatus.Rejected -> [ ScreeningGap.StatusNotCleared read.Status ], true
                | ScreeningStatus.Other status -> [ ScreeningGap.UnknownStatus status ], false

        let found, hitGaps =
            match hits with
            | Error failure -> [], [ ScreeningGap.HitListFailed failure ]
            | Ok list ->
                list.Hits,
                (list.Hits
                 |> List.choose (fun read ->
                     match read.ReviewStatus with
                     | HitStatus.Other status -> Some(ScreeningGap.UnknownHitStatus(read.Id, status))
                     | _ -> None))
                @ (list.Unparsed |> List.map ScreeningGap.UnreadableHit)

        let dismissed, toLookAt =
            found |> List.partition (fun read -> read.ReviewStatus = HitStatus.Dismissed)

        match screening, toLookAt, statusGaps @ hitGaps with
        | Ok read, [], [] ->
            EntityScreeningOutcome.Clear
                { ScreeningId = read.Id
                  ProgramId = read.SearchTerms.ProgramId
                  LegalName = read.SearchTerms.LegalName
                  AuditTrail = read.AuditTrail
                  DismissedHits = dismissed }
        | _, [], gaps when not reviewAsked -> EntityScreeningOutcome.NotScreened gaps
        | _, hits, gaps -> EntityScreeningOutcome.NeedsReview(hits, gaps)
