/// Plaid users: the customer record Payment Initiation, Income and Consumer Report attach to.
module Plaid.Users

open System
open System.Text.Json

/// How a request names the Plaid user. Printing hides a user token.
[<StructuredFormatDisplay("{Display}")>]
type UserRef =
    /// user_id from /user/create; integrations created from 2025-12-10 on use it.
    | UserId of string
    /// user_token of integrations created before 2025-12-10.
    | UserToken of string

    member this.Display =
        match this with
        | UserId userId -> $"UserId %s{userId}"
        | UserToken _ -> "UserToken ***"

    override this.ToString() = this.Display

module UserRef =

    /// The Link token field naming this user: ("user_id", id) or ("user_token", token).
    let linkField (user: UserRef) : string * obj =
        match user with
        | UserId userId -> "user_id", box userId
        | UserToken token -> "user_token", box token

    let internal userId =
        function
        | UserId userId -> ValueSome userId
        | UserToken _ -> ValueNone

    let internal userToken =
        function
        | UserToken token -> ValueSome token
        | UserId _ -> ValueNone

type UserContact = { Data: string; Primary: bool }

type UserAddress =
    { Street1: string voption
      Street2: string voption
      City: string voption
      Region: string voption
      PostalCode: string voption
      /// ISO 3166-1 alpha-2.
      Country: string
      Primary: bool }

/// The customer's identity. Payment Initiation needs the name and at least one email or phone number;
/// Consumer Report needs all of them with a primary entry each.
type UserIdentity =
    { GivenName: string
      FamilyName: string
      DateOfBirth: DateTime voption
      Emails: UserContact list
      PhoneNumbers: UserContact list
      Addresses: UserAddress list }

module UserIdentity =

    let create givenName familyName =
        { GivenName = givenName
          FamilyName = familyName
          DateOfBirth = ValueNone
          Emails = []
          PhoneNumbers = []
          Addresses = [] }

/// The identity shape of integrations created before 2025-12-10 (consumer_report_user_identity).
type LegacyUserIdentity =
    { FirstName: string
      LastName: string
      PhoneNumbers: string list
      Emails: string list
      DateOfBirth: DateTime
      Street: string
      City: string
      Region: string
      PostalCode: string
      Country: string }

/// Printing hides the user token.
[<StructuredFormatDisplay("{Display}")>]
type CreatedUser =
    { UserId: string
      /// Returned only to integrations created before 2025-12-10.
      UserToken: string voption }

    member this.Display = $"CreatedUser {{ UserId = %s{this.UserId}; UserToken = *** }}"
    override this.ToString() = this.Display

module CreatedUser =

    /// How later requests should name this user: the token where Plaid issued one, else the id.
    let toUserRef (user: CreatedUser) =
        match user.UserToken with
        | ValueSome token -> UserToken token
        | ValueNone -> UserId user.UserId

let private identityBody (identity: UserIdentity) =
    let contacts (items: UserContact list) =
        items |> List.map (fun c -> {| data = c.Data; primary = c.Primary |})

    {| name =
        {| given_name = identity.GivenName
           family_name = identity.FamilyName |}
       date_of_birth = identity.DateOfBirth |> ValueOption.map Request.isoDate
       emails = contacts identity.Emails
       phone_numbers = contacts identity.PhoneNumbers
       addresses =
        identity.Addresses
        |> List.map (fun a ->
            {| street_1 = a.Street1
               street_2 = a.Street2
               city = a.City
               region = a.Region
               postal_code = a.PostalCode
               country = a.Country
               primary = a.Primary |}) |}

let private createdUser (e: JsonElement) : CreatedUser =
    { UserId = Json.requiredString "user_id" e
      UserToken = Json.tryString "user_token" e }

/// Creates the Plaid user for clientUserId (the application's stable, non-identifying id). Plaid returns
/// the existing user when one already exists for clientUserId.
let createAsync (client: PlaidClient) (clientUserId: string) (identity: UserIdentity voption) ct =
    let body =
        {| client_user_id = clientUserId
           identity = identity |> ValueOption.map identityBody |}

    Api.postAsync client "/user/create" body createdUser ct

/// /user/create for integrations created before 2025-12-10, which send consumer_report_user_identity.
let createLegacyAsync (client: PlaidClient) (clientUserId: string) (identity: LegacyUserIdentity) ct =
    let body =
        {| client_user_id = clientUserId
           consumer_report_user_identity =
            {| first_name = identity.FirstName
               last_name = identity.LastName
               phone_numbers = identity.PhoneNumbers
               emails = identity.Emails
               date_of_birth = Request.isoDate identity.DateOfBirth
               primary_address =
                {| street = identity.Street
                   city = identity.City
                   region = identity.Region
                   postal_code = identity.PostalCode
                   country = identity.Country |} |} |}

    Api.postAsync client "/user/create" body createdUser ct

/// Replaces the fields identity gives; Plaid keeps the ones it leaves out.
let updateIdentityAsync (client: PlaidClient) (user: UserRef) (identity: UserIdentity) ct =
    let body =
        {| user_id = UserRef.userId user
           user_token = UserRef.userToken user
           identity = identityBody identity |}

    Api.postAsync client "/user/update" body ignore ct
