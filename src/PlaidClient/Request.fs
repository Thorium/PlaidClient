/// Request bodies: records serialized with System.Text.Json, where a ValueNone field or an empty list is
/// left out of the JSON instead of being sent as null or [].
module internal Plaid.Request

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Json.Serialization.Metadata

/// Writes ValueSome v as v and ValueNone as null (a dictionary entry keeps that null).
type private ValueOptionConverter<'T>() =
    inherit JsonConverter<'T voption>()

    override _.Read(_, _, _) =
        raise (NotSupportedException "Request bodies are only written.")

    override _.Write(writer, value, options) =
        match value with
        | ValueSome v -> JsonSerializer.Serialize(writer, v, options)
        | ValueNone -> writer.WriteNullValue()

type private ValueOptionConverterFactory() =
    inherit JsonConverterFactory()

    override _.CanConvert t =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<_ voption>

    override _.CreateConverter(t, _) =
        Activator.CreateInstance(typedefof<ValueOptionConverter<_>>.MakeGenericType(t.GetGenericArguments()))
        :?> JsonConverter

/// For a voption or F# list property, the test telling that its value is empty.
let private emptyTest (propertyType: Type) =
    let byProperty name =
        let property = propertyType.GetProperty(name: string)
        ValueSome(fun (value: obj) -> property.GetValue value :?> bool)

    if not propertyType.IsGenericType then
        ValueNone
    else
        match propertyType.GetGenericTypeDefinition() with
        | d when d = typedefof<_ voption> -> byProperty "IsNone"
        | d when d = typedefof<_ list> -> byProperty "IsEmpty"
        | _ -> ValueNone

let private leaveOutEmpty (typeInfo: JsonTypeInfo) =
    typeInfo.Properties
    |> Seq.iter (fun property ->
        match emptyTest property.PropertyType with
        | ValueSome isEmpty -> property.ShouldSerialize <- Func<obj, obj, bool>(fun _ value -> not (isEmpty value))
        | ValueNone -> ())

let serializerOptions =
    let resolver = DefaultJsonTypeInfoResolver()
    resolver.Modifiers.Add(Action<JsonTypeInfo> leaveOutEmpty)
    let options = JsonSerializerOptions(TypeInfoResolver = resolver)
    options.Converters.Add(ValueOptionConverterFactory())
    options

let serialize (body: obj) =
    JsonSerializer.Serialize(body, serializerOptions)

/// "yyyy-MM-dd", Plaid's date format.
let isoDate (d: DateTime) =
    d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

/// RFC 3339 in UTC, e.g. "2027-01-01T00:00:00Z".
let timestamp (d: DateTimeOffset) =
    d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

/// ValueSome true for a set flag; an unset one is left out, as Plaid defaults it to false.
let flag (isSet: bool) =
    if isSet then ValueSome true else ValueNone

/// Plaid's options object limiting a call to some accounts; left out for [].
let accountOptions (accountIds: string list) =
    if accountIds.IsEmpty then
        ValueNone
    else
        ValueSome {| account_ids = accountIds |}
