module MonitorTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Plaid
open Plaid.Monitor
open FakePlaid

let private ct = CancellationToken.None

let private expectOk =
    function
    | Ok v -> v
    | Error e -> failwith $"%A{e}"

let private strings (e: JsonElement) =
    e.EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq

let private auditJson =
    """{"source":"dashboard","dashboard_user_id":"dash_1","timestamp":"2026-10-30T09:15:00Z"}"""

let private screeningJson (id: string) (status: string) =
    $"""{{"id":"{id}","search_terms":{{"entity_watchlist_program_id":"entprg_1","legal_name":"Harbour Finch Ltd","document_number":"HF12345","email_address":"office@harbourfinch.example","country":"GB","phone_number":"+447700900123","url":"https://harbourfinch.example","version":2}},"assignee":"dash_2","status":"{status}","client_user_id":"company-7","audit_trail":{auditJson},"request_id":"r"}}"""

let private hitJson (id: string) (reviewStatus: string) =
    $"""{{"id":"{id}","review_status":"{reviewStatus}","first_active":"2026-01-02T00:00:00Z","inactive_since":null,"historical_since":"2026-03-04T00:00:00Z","list_code":"UK_HMC","plaid_uid":"uid_1","source_uid":"26192ABC","sub_programs":["SDGT"],"analysis":{{"documents":"no_input","email_addresses":"no_data","locations":"partial_match","names":"match","phone_numbers":"no_match","urls":"brand_new","search_terms_version":2}},"data":{{"names":[{{"analysis":{{"summary":"match"}},"data":{{"full":"Harbor Finch Limited","is_primary":true,"weak_alias_determination":"none"}}}}],"locations":[{{"analysis":{{"summary":"partial_match"}},"data":{{"full":"Port Example, GB","country":"GB"}}}}]}}}}"""

let private pageOf (items: string) (entries: string list) (next: string) =
    let joined = String.Join(",", entries)
    $"""{{"{items}":[{joined}],"next_cursor":{next},"request_id":"r"}}"""

let private programJson (id: string) =
    $"""{{"id":"{id}","created_at":"2026-01-01T00:00:00Z","is_rescanning_enabled":true,"lists_enabled":["UK_HMC","EU_CON","XX_NEW"],"name":"Applicants","name_sensitivity":"balanced","audit_trail":{auditJson},"is_archived":false,"request_id":"r"}}"""

let private reviewJson (id: string) =
    $"""{{"id":"{id}","confirmed_hits":["enthit_1"],"dismissed_hits":["enthit_2"],"comment":"Different company","audit_trail":{auditJson},"request_id":"r"}}"""

[<Fact>]
let ``entity screening create sends the search terms and client user id and parses the screening`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (screeningJson "entscr_1" "pending_review"))

        let terms =
            { EntitySearchTerms.create "entprg_1" "Harbour Finch Ltd" with
                Country = ValueSome "GB" }

        let! result = createEntityScreeningAsync (clientWith handler) terms "company-7" ct
        let screening = expectOk result
        Assert.Equal("entscr_1", screening.Id)
        Assert.Equal(ScreeningStatus.PendingReview, screening.Status)
        Assert.Equal("entprg_1", screening.SearchTerms.ProgramId)
        Assert.Equal("Harbour Finch Ltd", screening.SearchTerms.LegalName)
        Assert.Equal(ValueSome "HF12345", screening.SearchTerms.DocumentNumber)
        Assert.Equal(ValueSome "office@harbourfinch.example", screening.SearchTerms.EmailAddress)
        Assert.Equal(ValueSome "+447700900123", screening.SearchTerms.PhoneNumber)
        Assert.Equal(ValueSome "https://harbourfinch.example", screening.SearchTerms.Url)
        Assert.Equal(ValueSome 2, screening.SearchTermsVersion)
        Assert.Equal(ValueSome "dash_2", screening.Assignee)
        Assert.Equal(ValueSome "company-7", screening.ClientUserId)
        Assert.Equal(AuditSource.Dashboard, screening.AuditTrail.Source)
        Assert.Equal(ValueSome "dash_1", screening.AuditTrail.DashboardUserId)
        Assert.Equal(DateTimeOffset(2026, 10, 30, 9, 15, 0, TimeSpan.Zero), screening.AuditTrail.Timestamp)
        Assert.Contains("\"request_id\":\"r\"", screening.Raw)

        let call = Assert.Single handler.Calls
        Assert.Equal("/watchlist_screening/entity/create", call.Path)
        Assert.Equal("company-7", call.Body.GetProperty("client_user_id").GetString())
        let sent = call.Body.GetProperty "search_terms"
        Assert.Equal("entprg_1", sent.GetProperty("entity_watchlist_program_id").GetString())
        Assert.Equal("Harbour Finch Ltd", sent.GetProperty("legal_name").GetString())
        Assert.Equal("GB", sent.GetProperty("country").GetString())
        Assert.False(sent.TryGetProperty "document_number" |> fst)
        Assert.False(sent.TryGetProperty "url" |> fst)
    }

[<Fact>]
let ``entity screening get sends the id and returns Plaid's error`` () : Task =
    task {
        use found = new FakePlaidHandler(fun _ _ -> ok (screeningJson "entscr_1" "cleared"))
        let! result = getEntityScreeningAsync (clientWith found) "entscr_1" ct
        Assert.Equal(ScreeningStatus.Cleared, (expectOk result).Status)
        let call = Assert.Single found.Calls
        Assert.Equal("/watchlist_screening/entity/get", call.Path)
        Assert.Equal("entscr_1", call.Body.GetProperty("entity_watchlist_screening_id").GetString())

        use refused = new FakePlaidHandler(fun _ _ -> plaidError 400 "INVALID_FIELD")

        match! getEntityScreeningAsync (clientWith refused) "entscr_1" ct with
        | Error(ApiError e) -> Assert.Equal("INVALID_FIELD", e.ErrorCode)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``entity screening list sends the filters and follows the cursor`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (pageOf "entity_watchlist_screenings" [ screeningJson "entscr_1" "cleared" ] "\"page-2\"")
                | ValueSome _ -> ok (pageOf "entity_watchlist_screenings" [ screeningJson "entscr_2" "rejected" ] "null"))

        let filter =
            { EntityScreeningFilter.all with
                ClientUserId = ValueSome "company-7"
                Status = ValueSome ScreeningStatus.PendingReview
                Assignee = ValueSome "dash_2" }

        let! result = listEntityScreeningsAsync (clientWith handler) "entprg_1" filter ct
        Assert.Equal<string list>([ "entscr_1"; "entscr_2" ], expectOk result |> List.map _.Id)

        match handler.Calls with
        | [ first; second ] ->
            Assert.Equal("/watchlist_screening/entity/list", first.Path)
            Assert.Equal("entprg_1", first.Body.GetProperty("entity_watchlist_program_id").GetString())
            Assert.Equal("company-7", first.Body.GetProperty("client_user_id").GetString())
            Assert.Equal("pending_review", first.Body.GetProperty("status").GetString())
            Assert.Equal("dash_2", first.Body.GetProperty("assignee").GetString())
            Assert.Equal(ValueNone, cursorOf first.Body)
            Assert.Equal(ValueSome "page-2", cursorOf second.Body)
            Assert.Equal("pending_review", second.Body.GetProperty("status").GetString())
        | calls -> failwith $"%A{calls}"
    }

[<Fact>]
let ``entity screening list without filters sends only the program`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (pageOf "entity_watchlist_screenings" [] "null"))
        let! result = listEntityScreeningsAsync (clientWith handler) "entprg_1" EntityScreeningFilter.all ct
        Assert.Empty(expectOk result)
        let body = (Assert.Single handler.Calls).Body

        Assert.Equal<string list>(
            [ "entity_watchlist_program_id" ],
            body.EnumerateObject() |> Seq.map _.Name |> List.ofSeq
        )
    }

[<Fact>]
let ``entity screening update sends only what changes`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (screeningJson "entscr_1" "cleared"))
        let client = clientWith handler

        let update =
            { EntityScreeningUpdate.create "entscr_1" with
                SearchTerms =
                    ValueSome
                        { EntitySearchTermsUpdate.create "entprg_2" with
                            LegalName = ValueSome "Harbour Finch Holdings Ltd" }
                Assignee = ValueSome "dash_3"
                Status = ValueSome ScreeningStatus.Rejected
                ClientUserId = ValueSome "company-8"
                ResetAssignee = true }

        let! full = updateEntityScreeningAsync client update ct
        Assert.True(Result.isOk full)
        let! bare = updateEntityScreeningAsync client (EntityScreeningUpdate.create "entscr_1") ct
        Assert.True(Result.isOk bare)

        match handler.Calls with
        | [ first; second ] ->
            Assert.Equal("/watchlist_screening/entity/update", first.Path)
            Assert.Equal("entscr_1", first.Body.GetProperty("entity_watchlist_screening_id").GetString())
            let terms = first.Body.GetProperty "search_terms"
            Assert.Equal("entprg_2", terms.GetProperty("entity_watchlist_program_id").GetString())
            Assert.Equal("Harbour Finch Holdings Ltd", terms.GetProperty("legal_name").GetString())
            Assert.False(terms.TryGetProperty "country" |> fst)
            Assert.Equal("dash_3", first.Body.GetProperty("assignee").GetString())
            Assert.Equal("rejected", first.Body.GetProperty("status").GetString())
            Assert.Equal("company-8", first.Body.GetProperty("client_user_id").GetString())
            Assert.Equal<string list>([ "assignee" ], strings (first.Body.GetProperty "reset_fields"))

            Assert.Equal<string list>(
                [ "entity_watchlist_screening_id" ],
                second.Body.EnumerateObject() |> Seq.map _.Name |> List.ofSeq
            )
        | calls -> failwith $"%A{calls}"
    }

[<Fact>]
let ``entity hit list parses a hit and keeps unknown values`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok (pageOf "entity_watchlist_screening_hits" [ hitJson "enthit_1" "pending_review" ] "null"))

        let! result = listEntityHitsAsync (clientWith handler) "entscr_1" ct
        let list = expectOk result
        Assert.Empty list.Unparsed
        let found = Assert.Single list.Hits
        Assert.Equal("enthit_1", found.Id)
        Assert.Equal(HitStatus.PendingReview, found.ReviewStatus)
        Assert.Equal(EntityListCode.UkHmc, found.ListCode)
        Assert.Equal(ValueSome "uid_1", found.PlaidUid)
        Assert.Equal(ValueSome "26192ABC", found.SourceUid)
        Assert.Equal<string list>([ "SDGT" ], found.SubPrograms)
        Assert.Equal(ValueSome(DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)), found.FirstActive)
        Assert.Equal(ValueNone, found.InactiveSince)
        Assert.Equal(ValueSome(DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero)), found.HistoricalSince)

        Assert.Equal(
            ValueSome
                { Names = ValueSome MatchSummary.Match
                  Documents = ValueSome MatchSummary.NoInput
                  Locations = ValueSome MatchSummary.PartialMatch
                  EmailAddresses = ValueSome MatchSummary.NoData
                  PhoneNumbers = ValueSome MatchSummary.NoMatch
                  Urls = ValueSome(MatchSummary.Other "brand_new")
                  SearchTermsVersion = ValueSome 2 },
            found.Analysis
        )

        let name = Assert.Single found.Names
        Assert.Equal("Harbor Finch Limited", name.Full)
        Assert.True name.IsPrimary
        Assert.Equal(ValueSome MatchSummary.Match, name.Summary)
        Assert.Contains("Port Example, GB", found.Raw)

        let call = Assert.Single handler.Calls
        Assert.Equal("/watchlist_screening/entity/hit/list", call.Path)
        Assert.Equal("entscr_1", call.Body.GetProperty("entity_watchlist_screening_id").GetString())
    }

[<Fact>]
let ``a hit without analysis or data still reads`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ ->
                ok (pageOf "entity_watchlist_screening_hits" [ """{"id":"enthit_1","review_status":"confirmed","list_code":"XX_NEW"}""" ] "null"))

        let! result = listEntityHitsAsync (clientWith handler) "entscr_1" ct
        let found = Assert.Single (expectOk result).Hits
        Assert.Equal(EntityListCode.Other "XX_NEW", found.ListCode)
        Assert.Equal(ValueNone, found.Analysis)
        Assert.Empty found.Names
    }

[<Theory>]
[<InlineData("""{"review_status":"dismissed","list_code":"UK_HMC"}""", "id")>]
[<InlineData("""{"id":"enthit_2","list_code":"UK_HMC"}""", "review_status")>]
[<InlineData("""{"id":"enthit_2","review_status":null,"list_code":"UK_HMC"}""", "review_status")>]
[<InlineData("""{"id":"enthit_2","review_status":7,"list_code":"UK_HMC"}""", "review_status")>]
[<InlineData("""{"id":"enthit_2","review_status":"dismissed"}""", "list_code")>]
[<InlineData("""42""", "id")>]
let ``a hit that can't be read is in Unparsed with its raw JSON, the others read`` (bad: string, field: string) : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok (pageOf "entity_watchlist_screening_hits" [ hitJson "enthit_1" "dismissed"; bad ] "null"))

        let! result = listEntityHitsAsync (clientWith handler) "entscr_1" ct
        let list = expectOk result
        Assert.Equal("enthit_1", (Assert.Single list.Hits).Id)
        let unparsed = Assert.Single list.Unparsed
        Assert.Contains(field, unparsed.Reason)
        Assert.Equal(bad, unparsed.Raw)
    }

[<Fact>]
let ``printing an unreadable hit hides its raw JSON`` () =
    let unparsed = { Reason = "Missing"; Raw = "office@harbourfinch.example" }
    Assert.DoesNotContain("harbourfinch", $"%A{unparsed}")
    Assert.DoesNotContain("harbourfinch", unparsed.ToString())

[<Fact>]
let ``entity review create sends both hit lists, even empty, and parses the review`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (reviewJson "entrev_1"))
        let client = clientWith handler
        let! result = createEntityReviewAsync client "entscr_1" [ "enthit_1" ] [] (ValueSome "A valid match") ct
        let review = expectOk result
        Assert.Equal("entrev_1", review.Id)
        Assert.Equal<string list>([ "enthit_1" ], review.ConfirmedHits)
        Assert.Equal<string list>([ "enthit_2" ], review.DismissedHits)
        Assert.Equal(ValueSome "Different company", review.Comment)
        Assert.Equal(AuditSource.Dashboard, review.AuditTrail.Source)
        Assert.Contains("entrev_1", review.Raw)

        let! _ = createEntityReviewAsync client "entscr_1" [] [] ValueNone ct

        match handler.Calls with
        | [ withHits; empty ] ->
            Assert.Equal("/watchlist_screening/entity/review/create", withHits.Path)
            Assert.Equal("entscr_1", withHits.Body.GetProperty("entity_watchlist_screening_id").GetString())
            Assert.Equal<string list>([ "enthit_1" ], strings (withHits.Body.GetProperty "confirmed_hits"))
            Assert.Equal<string list>([], strings (withHits.Body.GetProperty "dismissed_hits"))
            Assert.Equal("A valid match", withHits.Body.GetProperty("comment").GetString())
            Assert.Equal<string list>([], strings (empty.Body.GetProperty "confirmed_hits"))
            Assert.Equal<string list>([], strings (empty.Body.GetProperty "dismissed_hits"))
            Assert.False(empty.Body.TryGetProperty "comment" |> fst)
        | calls -> failwith $"%A{calls}"
    }

[<Fact>]
let ``a review with a null comment parses, one without its hit lists does not`` () : Task =
    task {
        use nullComment =
            new FakePlaidHandler(fun _ _ ->
                ok (pageOf "entity_watchlist_screening_reviews" [ $"""{{"id":"entrev_1","confirmed_hits":[],"dismissed_hits":[],"comment":null,"audit_trail":{auditJson}}}""" ] "null"))

        let! read = listEntityReviewsAsync (clientWith nullComment) "entscr_1" ct
        Assert.Equal(ValueNone, (Assert.Single(expectOk read)).Comment)

        use noLists =
            new FakePlaidHandler(fun _ _ ->
                ok (pageOf "entity_watchlist_screening_reviews" [ $"""{{"id":"entrev_1","comment":null,"audit_trail":{auditJson}}}""" ] "null"))

        match! listEntityReviewsAsync (clientWith noLists) "entscr_1" ct with
        | Error(InvalidResponse message) -> Assert.Contains("confirmed_hits", message)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``entity review list follows the cursor`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (pageOf "entity_watchlist_screening_reviews" [ reviewJson "entrev_1" ] "\"page-2\"")
                | ValueSome _ -> ok (pageOf "entity_watchlist_screening_reviews" [ reviewJson "entrev_2" ] "null"))

        let! result = listEntityReviewsAsync (clientWith handler) "entscr_1" ct
        Assert.Equal<string list>([ "entrev_1"; "entrev_2" ], expectOk result |> List.map _.Id)

        match handler.Calls with
        | [ first; second ] ->
            Assert.Equal("/watchlist_screening/entity/review/list", first.Path)
            Assert.Equal("entscr_1", first.Body.GetProperty("entity_watchlist_screening_id").GetString())
            Assert.Equal(ValueSome "page-2", cursorOf second.Body)
        | calls -> failwith $"%A{calls}"
    }

[<Fact>]
let ``entity history list follows the cursor and is empty for a screening never edited`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (pageOf "entity_watchlist_screenings" [ screeningJson "entscr_1" "pending_review" ] "\"page-2\"")
                | ValueSome _ -> ok (pageOf "entity_watchlist_screenings" [ screeningJson "entscr_1" "cleared" ] "null"))

        let! result = listEntityHistoryAsync (clientWith handler) "entscr_1" ct

        Assert.Equal<ScreeningStatus list>(
            [ ScreeningStatus.PendingReview; ScreeningStatus.Cleared ],
            expectOk result |> List.map _.Status
        )

        let first = handler.Calls.Head
        Assert.Equal("/watchlist_screening/entity/history/list", first.Path)
        Assert.Equal("entscr_1", first.Body.GetProperty("entity_watchlist_screening_id").GetString())

        use never = new FakePlaidHandler(fun _ _ -> ok (pageOf "entity_watchlist_screenings" [] "null"))
        let! none = listEntityHistoryAsync (clientWith never) "entscr_1" ct
        Assert.Empty(expectOk none)
    }

[<Fact>]
let ``entity program get parses its lists and settings`` () : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (programJson "entprg_1"))
        let! result = getEntityProgramAsync (clientWith handler) "entprg_1" ct
        let program = expectOk result
        Assert.Equal("entprg_1", program.Id)
        Assert.Equal("Applicants", program.Name)
        Assert.Equal(DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), program.CreatedAt)
        Assert.True program.IsRescanningEnabled
        Assert.False program.IsArchived
        Assert.Equal(NameSensitivity.Balanced, program.NameSensitivity)

        Assert.Equal<EntityListCode list>(
            [ EntityListCode.UkHmc; EntityListCode.EuCon; EntityListCode.Other "XX_NEW" ],
            program.ListsEnabled
        )

        Assert.Equal(ValueSome "dash_1", program.AuditTrail.DashboardUserId)
        Assert.Contains("XX_NEW", program.Raw)
        let call = Assert.Single handler.Calls
        Assert.Equal("/watchlist_screening/entity/program/get", call.Path)
        Assert.Equal("entprg_1", call.Body.GetProperty("entity_watchlist_program_id").GetString())
    }

[<Fact>]
let ``a program without its lists is an invalid response, not one with no lists`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok ((programJson "entprg_1").Replace("""lists_enabled":["UK_HMC","EU_CON","XX_NEW"],""", """other":[],""")))

        match! getEntityProgramAsync (clientWith handler) "entprg_1" ct with
        | Error(InvalidResponse message) -> Assert.Contains("lists_enabled", message)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``entity program list follows the cursor and starts with an empty body`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (pageOf "entity_watchlist_programs" [ programJson "entprg_1" ] "\"page-2\"")
                | ValueSome _ -> ok (pageOf "entity_watchlist_programs" [ programJson "entprg_2" ] "null"))

        let! result = listEntityProgramsAsync (clientWith handler) ct
        Assert.Equal<string list>([ "entprg_1"; "entprg_2" ], expectOk result |> List.map _.Id)

        match handler.Calls with
        | [ first; second ] ->
            Assert.Equal("/watchlist_screening/entity/program/list", first.Path)
            Assert.Empty(first.Body.EnumerateObject())
            Assert.Equal(ValueSome "page-2", cursorOf second.Body)
        | calls -> failwith $"%A{calls}"
    }

[<Theory>]
[<InlineData("cleared")>]
[<InlineData("pending_review")>]
[<InlineData("rejected")>]
[<InlineData("under_appeal")>]
let ``screening status round-trips, an unknown one as Other`` (code: string) =
    Assert.Equal(code, ScreeningStatus.toString (ScreeningStatus.ofString code))

[<Fact>]
let ``unknown codes are kept as Other`` () =
    Assert.Equal(ScreeningStatus.Other "under_appeal", ScreeningStatus.ofString "under_appeal")
    Assert.Equal(HitStatus.Other "escalated", HitStatus.ofString "escalated")
    Assert.Equal(AuditSource.Other "batch", AuditSource.ofString "batch")
    Assert.Equal(EntityListCode.Other "XX_NEW", EntityListCode.ofString "XX_NEW")
    Assert.Equal(MatchSummary.Other "close", MatchSummary.ofString "close")
    Assert.Equal(NameSensitivity.Other "loose", NameSensitivity.ofString "loose")

[<Theory>]
[<InlineData("confirmed")>]
[<InlineData("pending_review")>]
[<InlineData("dismissed")>]
[<InlineData("escalated")>]
let ``hit status round-trips`` (code: string) =
    Assert.Equal(code, HitStatus.toString (HitStatus.ofString code))

[<Theory>]
[<InlineData("dashboard")>]
[<InlineData("link")>]
[<InlineData("api")>]
[<InlineData("system")>]
[<InlineData("retro")>]
[<InlineData("batch")>]
let ``audit source round-trips`` (code: string) =
    Assert.Equal(code, AuditSource.toString (AuditSource.ofString code))

[<Theory>]
[<InlineData("CA_CON")>]
[<InlineData("EU_CON")>]
[<InlineData("IZ_SOE")>]
[<InlineData("IZ_UNC")>]
[<InlineData("IZ_WBK")>]
[<InlineData("US_CAP")>]
[<InlineData("US_FSE")>]
[<InlineData("US_MBS")>]
[<InlineData("US_SDN")>]
[<InlineData("US_SSI")>]
[<InlineData("US_CMC")>]
[<InlineData("US_UVL")>]
[<InlineData("US_SAM")>]
[<InlineData("US_TEL")>]
[<InlineData("AU_CON")>]
[<InlineData("UK_HMC")>]
let ``every list code in Plaid's spec has its own case`` (code: string) =
    let list = EntityListCode.ofString code
    Assert.Equal(code, EntityListCode.toString list)

    match list with
    | EntityListCode.Other _ -> failwith code
    | _ -> ()

// Paging. Every paged call shares these rules, so each runs through all of them.

type PagedCall =
    | Screenings
    | Hits
    | Reviews
    | History
    | Programs

let private itemsOf =
    function
    | Screenings
    | History -> "entity_watchlist_screenings"
    | Hits -> "entity_watchlist_screening_hits"
    | Reviews -> "entity_watchlist_screening_reviews"
    | Programs -> "entity_watchlist_programs"

let private entryOf =
    function
    | Screenings
    | History -> screeningJson "entscr_1" "cleared"
    | Hits -> hitJson "enthit_1" "dismissed"
    | Reviews -> reviewJson "entrev_1"
    | Programs -> programJson "entprg_1"

/// The call's answer as a count of entries, or its failure.
let private run call client =
    task {
        match call with
        | Screenings ->
            let! r = listEntityScreeningsAsync client "entprg_1" EntityScreeningFilter.all ct
            return r |> Result.map List.length
        | Hits ->
            let! r = listEntityHitsAsync client "entscr_1" ct
            return r |> Result.map _.Hits.Length
        | Reviews ->
            let! r = listEntityReviewsAsync client "entscr_1" ct
            return r |> Result.map List.length
        | History ->
            let! r = listEntityHistoryAsync client "entscr_1" ct
            return r |> Result.map List.length
        | Programs ->
            let! r = listEntityProgramsAsync client ct
            return r |> Result.map List.length
    }

let pagedCalls: TheoryData<PagedCall> =
    TheoryData<PagedCall>([ Screenings; Hits; Reviews; History; Programs ])

[<Theory; MemberData(nameof pagedCalls)>]
let ``all pages are read, in order`` (call: PagedCall) : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (pageOf (itemsOf call) [ entryOf call ] "\"page-2\"")
                | ValueSome "page-2" -> ok (pageOf (itemsOf call) [ entryOf call; entryOf call ] "\"page-3\"")
                | ValueSome _ -> ok (pageOf (itemsOf call) [ entryOf call ] "null"))

        let! result = run call (clientWith handler)
        Assert.Equal(Ok 4, result)

        Assert.Equal<string voption list>(
            [ ValueNone; ValueSome "page-2"; ValueSome "page-3" ],
            handler.Calls |> List.map (fun c -> cursorOf c.Body)
        )
    }

[<Theory; MemberData(nameof pagedCalls)>]
let ``a failed later page fails the whole list`` (call: PagedCall) : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (pageOf (itemsOf call) [ entryOf call ] "\"page-2\"")
                | ValueSome _ -> plaidError 500 "INTERNAL_SERVER_ERROR")

        match! run call (clientWith handler) with
        | Error(ApiError e) -> Assert.Equal("INTERNAL_SERVER_ERROR", e.ErrorCode)
        | other -> failwith $"%A{other}"

        Assert.Equal(2, handler.Calls.Length)
    }

[<Theory; MemberData(nameof pagedCalls)>]
let ``a later page that can't be read fails the whole list`` (call: PagedCall) : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ body ->
                match cursorOf body with
                | ValueNone -> ok (pageOf (itemsOf call) [ entryOf call ] "\"page-2\"")
                | ValueSome _ -> ok "not json")

        match! run call (clientWith handler) with
        | Error(InvalidResponse _) -> ()
        | other -> failwith $"%A{other}"
    }

[<Theory; MemberData(nameof pagedCalls)>]
let ``a page without next_cursor can't be read: the list might go on`` (call: PagedCall) : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok $"""{{"{itemsOf call}":[{entryOf call}],"request_id":"r"}}""")

        match! run call (clientWith handler) with
        | Error(InvalidResponse message) -> Assert.Contains("next_cursor", message)
        | other -> failwith $"%A{other}"
    }

[<Theory; MemberData(nameof pagedCalls)>]
let ``a next_cursor of another type can't be read`` (call: PagedCall) : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (pageOf (itemsOf call) [ entryOf call ] "12"))

        match! run call (clientWith handler) with
        | Error(InvalidResponse message) -> Assert.Contains("next_cursor", message)
        | other -> failwith $"%A{other}"
    }

[<Theory; MemberData(nameof pagedCalls)>]
let ``a page without its array can't be read: it isn't an empty list`` (call: PagedCall) : Task =
    task {
        for reply in [ """{"next_cursor":null,"request_id":"r"}"""; $"""{{"{itemsOf call}":null,"next_cursor":null}}"""; "[]" ] do
            use handler = new FakePlaidHandler(fun _ _ -> ok reply)

            match! run call (clientWith handler) with
            | Error(InvalidResponse message) -> Assert.Contains(itemsOf call, message)
            | other -> failwith $"%A{other}"
    }

[<Theory; MemberData(nameof pagedCalls)>]
let ``a cursor Plaid already gave fails the list rather than looping`` (call: PagedCall) : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (pageOf (itemsOf call) [ entryOf call ] "\"page-2\""))

        match! run call (clientWith handler) with
        | Error(InvalidResponse message) -> Assert.Contains("repeated next_cursor", message)
        | other -> failwith $"%A{other}"

        Assert.Equal(2, handler.Calls.Length)
    }

[<Theory>]
[<InlineData("null")>]
[<InlineData("\"\"")>]
let ``a null or empty next_cursor ends the list`` (next: string) : Task =
    task {
        use handler = new FakePlaidHandler(fun _ _ -> ok (pageOf "entity_watchlist_programs" [ programJson "entprg_1" ] next))
        let! result = listEntityProgramsAsync (clientWith handler) ct
        Assert.Equal(1, (expectOk result).Length)
        Assert.Single handler.Calls |> ignore
    }

[<Fact>]
let ``a screening in a page that can't be read fails the page`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok (pageOf "entity_watchlist_screenings" [ """{"id":"entscr_1"}""" ] "null"))

        match! listEntityScreeningsAsync (clientWith handler) "entprg_1" EntityScreeningFilter.all ct with
        | Error(InvalidResponse _) -> ()
        | other -> failwith $"%A{other}"
    }

// The webhook.

[<Fact>]
let ``entity screening webhook parses by its own id field`` () =
    Assert.Equal(
        Ok(Webhook.EntityScreeningStatusUpdated "entscr_1"),
        Webhook.parse
            """{"webhook_type":"ENTITY_SCREENING","webhook_code":"STATUS_UPDATED","entity_screening_id":"entscr_1","environment":"production"}"""
    )

[<Fact>]
let ``entity screening webhook without its id is refused, another code stays Other`` () =
    match
        Webhook.parse
            """{"webhook_type":"ENTITY_SCREENING","webhook_code":"STATUS_UPDATED","entity_watchlist_screening_id":"entscr_1"}"""
    with
    | Error(InvalidResponse message) -> Assert.Contains("entity_screening_id", message)
    | other -> failwith $"%A{other}"

    Assert.Equal(
        Ok(Webhook.Other("ENTITY_SCREENING", "NEW_CODE", ValueNone)),
        Webhook.parse """{"webhook_type":"ENTITY_SCREENING","webhook_code":"NEW_CODE","entity_screening_id":"entscr_1"}"""
    )

    Assert.Equal(
        Ok(Webhook.Other("SCREENING", "STATUS_UPDATED", ValueNone)),
        Webhook.parse """{"webhook_type":"SCREENING","webhook_code":"STATUS_UPDATED","screening_id":"scr_1"}"""
    )

// The outcome.

let private screeningWith status =
    use handler = new FakePlaidHandler(fun _ _ -> ok (screeningJson "entscr_1" status))
    (getEntityScreeningAsync (clientWith handler) "entscr_1" ct).Result |> expectOk

let private hitsWith (entries: string list) =
    use handler = new FakePlaidHandler(fun _ _ -> ok (pageOf "entity_watchlist_screening_hits" entries "null"))
    (listEntityHitsAsync (clientWith handler) "entscr_1" ct).Result |> expectOk

let private outcomeOf status (entries: string list) =
    EntityScreeningOutcome.ofResults (Ok(screeningWith status)) (Ok(hitsWith entries))

let private failure = ApiError { HttpStatus = 500; ErrorType = "API_ERROR"; ErrorCode = "INTERNAL_SERVER_ERROR"; ErrorMessage = ""; DisplayMessage = ValueNone; RequestId = ValueNone }

[<Fact>]
let ``cleared with no hit reads Clear, carrying its program, name and audit trail`` () =
    match outcomeOf "cleared" [] with
    | EntityScreeningOutcome.Clear clear ->
        Assert.Equal("entscr_1", clear.ScreeningId)
        Assert.Equal("entprg_1", clear.ProgramId)
        Assert.Equal("Harbour Finch Ltd", clear.LegalName)
        Assert.Equal(AuditSource.Dashboard, clear.AuditTrail.Source)
        Assert.Equal(ValueSome "dash_1", clear.AuditTrail.DashboardUserId)
        Assert.Equal(DateTimeOffset(2026, 10, 30, 9, 15, 0, TimeSpan.Zero), clear.AuditTrail.Timestamp)
        Assert.Empty clear.DismissedHits
    | other -> failwith $"%A{other}"

[<Fact>]
let ``cleared with every hit dismissed reads Clear, carrying those hits`` () =
    match outcomeOf "cleared" [ hitJson "enthit_1" "dismissed"; hitJson "enthit_2" "dismissed" ] with
    | EntityScreeningOutcome.Clear clear ->
        Assert.Equal<string list>([ "enthit_1"; "enthit_2" ], clear.DismissedHits |> List.map _.Id)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``cleared with a pending hit needs review`` () =
    match outcomeOf "cleared" [ hitJson "enthit_1" "dismissed"; hitJson "enthit_2" "pending_review" ] with
    | EntityScreeningOutcome.NeedsReview(hits, []) -> Assert.Equal("enthit_2", (Assert.Single hits).Id)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``cleared with a confirmed hit needs review`` () =
    match outcomeOf "cleared" [ hitJson "enthit_1" "confirmed" ] with
    | EntityScreeningOutcome.NeedsReview(hits, []) -> Assert.Equal("enthit_1", (Assert.Single hits).Id)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``pending review with no hit still needs review`` () =
    match outcomeOf "pending_review" [] with
    | EntityScreeningOutcome.NeedsReview([], [ ScreeningGap.StatusNotCleared ScreeningStatus.PendingReview ]) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``pending review with every hit dismissed still needs review`` () =
    match outcomeOf "pending_review" [ hitJson "enthit_1" "dismissed" ] with
    | EntityScreeningOutcome.NeedsReview([], [ ScreeningGap.StatusNotCleared ScreeningStatus.PendingReview ]) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``rejected needs review, with or without hits`` () =
    match outcomeOf "rejected" [] with
    | EntityScreeningOutcome.NeedsReview([], [ ScreeningGap.StatusNotCleared ScreeningStatus.Rejected ]) -> ()
    | other -> failwith $"%A{other}"

    match outcomeOf "rejected" [ hitJson "enthit_1" "confirmed" ] with
    | EntityScreeningOutcome.NeedsReview([ found ], [ ScreeningGap.StatusNotCleared ScreeningStatus.Rejected ]) ->
        Assert.Equal("enthit_1", found.Id)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an unknown status with no hit is not screened`` () =
    match outcomeOf "under_appeal" [] with
    | EntityScreeningOutcome.NotScreened [ ScreeningGap.UnknownStatus "under_appeal" ] -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an unknown status with a pending hit needs review, naming the status`` () =
    match outcomeOf "under_appeal" [ hitJson "enthit_1" "pending_review" ] with
    | EntityScreeningOutcome.NeedsReview([ _ ], [ ScreeningGap.UnknownStatus "under_appeal" ]) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an unknown hit status under cleared needs review, naming the hit`` () =
    match outcomeOf "cleared" [ hitJson "enthit_1" "escalated" ] with
    | EntityScreeningOutcome.NeedsReview([ found ], [ ScreeningGap.UnknownHitStatus("enthit_1", "escalated") ]) ->
        Assert.Equal(HitStatus.Other "escalated", found.ReviewStatus)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a hit that can't be read under cleared is not screened, keeping the raw JSON`` () =
    let bad = """{"id":"enthit_2","list_code":"UK_HMC"}"""

    match outcomeOf "cleared" [ hitJson "enthit_1" "dismissed"; bad ] with
    | EntityScreeningOutcome.NotScreened [ ScreeningGap.UnreadableHit unparsed ] -> Assert.Equal(bad, unparsed.Raw)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a failed hit list under cleared is not screened`` () =
    match EntityScreeningOutcome.ofResults (Ok(screeningWith "cleared")) (Error failure) with
    | EntityScreeningOutcome.NotScreened [ ScreeningGap.HitListFailed found ] -> Assert.Equal(failure, found)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a failed hit list under pending review needs review and names the failure`` () =
    match EntityScreeningOutcome.ofResults (Ok(screeningWith "pending_review")) (Error failure) with
    | EntityScreeningOutcome.NeedsReview([], [ ScreeningGap.StatusNotCleared _; ScreeningGap.HitListFailed _ ]) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a screening that failed is not screened, even with no hit`` () =
    match EntityScreeningOutcome.ofResults (Error failure) (Ok(hitsWith [])) with
    | EntityScreeningOutcome.NotScreened [ ScreeningGap.ScreeningReadFailed found ] -> Assert.Equal(failure, found)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a screening that failed with a pending hit needs review`` () =
    match EntityScreeningOutcome.ofResults (Error failure) (Ok(hitsWith [ hitJson "enthit_1" "pending_review" ])) with
    | EntityScreeningOutcome.NeedsReview([ _ ], [ ScreeningGap.ScreeningReadFailed _ ]) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``both failing is not screened, naming both`` () =
    match EntityScreeningOutcome.ofResults (Error failure) (Error failure) with
    | EntityScreeningOutcome.NotScreened [ ScreeningGap.ScreeningReadFailed _; ScreeningGap.HitListFailed _ ] -> ()
    | other -> failwith $"%A{other}"

[<Theory>]
[<InlineData("status")>]
[<InlineData("audit_trail")>]
[<InlineData("search_terms")>]
let ``a screening without what a Clear needs is an invalid response, and so not screened`` (field: string) : Task =
    task {
        let answer =
            use doc = JsonDocument.Parse(screeningJson "entscr_1" "cleared")

            let kept =
                doc.RootElement.EnumerateObject()
                |> Seq.filter (fun p -> p.Name <> field)
                |> Seq.map (fun p -> $"\"{p.Name}\":{p.Value.GetRawText()}")

            "{" + String.Join(",", kept) + "}"

        use handler = new FakePlaidHandler(fun _ _ -> ok answer)
        let! screening = getEntityScreeningAsync (clientWith handler) "entscr_1" ct

        match screening with
        | Error(InvalidResponse message) -> Assert.Contains(field, message)
        | other -> failwith $"%A{other}"

        match EntityScreeningOutcome.ofResults screening (Ok(hitsWith [])) with
        | EntityScreeningOutcome.NotScreened [ ScreeningGap.ScreeningReadFailed _ ] -> ()
        | other -> failwith $"%A{other}"
    }

[<Theory>]
[<InlineData("""{"source":"dashboard","dashboard_user_id":"d"}""", "timestamp")>]
[<InlineData("""{"source":"dashboard","timestamp":"yesterday"}""", "timestamp")>]
[<InlineData("""{"dashboard_user_id":"d","timestamp":"2026-10-30T09:15:00Z"}""", "source")>]
let ``an audit trail without its date or source is an invalid response`` (audit: string, field: string) : Task =
    task {
        use handler =
            new FakePlaidHandler(fun _ _ -> ok ((screeningJson "entscr_1" "cleared").Replace(auditJson, audit)))

        match! getEntityScreeningAsync (clientWith handler) "entscr_1" ct with
        | Error(InvalidResponse message) -> Assert.Contains(field, message)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``an audit source Plaid hasn't listed stays on the Clear as Other`` () =
    let answer =
        (screeningJson "entscr_1" "cleared").Replace("\"source\":\"dashboard\"", "\"source\":\"batch\"")

    use handler = new FakePlaidHandler(fun _ _ -> ok answer)
    let screening = (getEntityScreeningAsync (clientWith handler) "entscr_1" ct).Result

    match EntityScreeningOutcome.ofResults screening (Ok(hitsWith [])) with
    | EntityScreeningOutcome.Clear clear -> Assert.Equal(AuditSource.Other "batch", clear.AuditTrail.Source)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``the outcome reads through the real calls, a failed hit page included`` () : Task =
    task {
        use handler =
            new FakePlaidHandler(fun path body ->
                match path, cursorOf body with
                | "/watchlist_screening/entity/get", _ -> ok (screeningJson "entscr_1" "cleared")
                | _, ValueNone -> ok (pageOf "entity_watchlist_screening_hits" [ hitJson "enthit_1" "dismissed" ] "\"page-2\"")
                | _, ValueSome _ -> 503, "<html>Bad gateway</html>")

        let client = clientWith handler
        let! screening = getEntityScreeningAsync client "entscr_1" ct
        let! hits = listEntityHitsAsync client "entscr_1" ct

        match EntityScreeningOutcome.ofResults screening hits with
        | EntityScreeningOutcome.NotScreened [ ScreeningGap.HitListFailed(HttpError(503, _)) ] -> ()
        | other -> failwith $"%A{other}"
    }
