/// Readers over JsonElement. Optional readers treat a missing property and JSON null alike.
module internal Plaid.Json

open System
open System.Globalization
open System.Text.Json

/// A required field is missing or has the wrong type.
exception InvalidJson of string

let tryProp (name: string) (e: JsonElement) =
    if e.ValueKind <> JsonValueKind.Object then
        ValueNone
    else
        match e.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> ValueSome v
        | _ -> ValueNone

let private ofKind kind (read: JsonElement -> 'T) name e =
    tryProp name e
    |> ValueOption.bind (fun v -> if v.ValueKind = kind then ValueSome(read v) else ValueNone)

let tryString name e = ofKind JsonValueKind.String _.GetString() name e

/// ValueNone also when the number does not fit the type.
let private tryNumber (read: JsonElement -> bool * 'T) name e =
    ofKind JsonValueKind.Number id name e
    |> ValueOption.bind (fun v ->
        match read v with
        | true, n -> ValueSome n
        | _ -> ValueNone)

let tryDecimal name e = tryNumber (fun v -> v.TryGetDecimal()) name e

let tryInt64 name e = tryNumber (fun v -> v.TryGetInt64()) name e

let tryInt name e = tryNumber (fun v -> v.TryGetInt32()) name e

let tryBool name e =
    tryProp name e
    |> ValueOption.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.True -> ValueSome true
        | JsonValueKind.False -> ValueSome false
        | _ -> ValueNone)

let private parseDate (s: string) =
    match DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, d -> ValueSome d
    | _ -> ValueNone

let private parseTimestamp (s: string) =
    match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
    | true, d -> ValueSome d
    | _ -> ValueNone

let tryDate name e = tryString name e |> ValueOption.bind parseDate

let tryTimestamp name e = tryString name e |> ValueOption.bind parseTimestamp

let tryObject name e = ofKind JsonValueKind.Object id name e

/// Elements of an array property; empty when the property is missing or null.
let array name e =
    match ofKind JsonValueKind.Array id name e with
    | ValueSome v -> v.EnumerateArray() |> List.ofSeq
    | ValueNone -> []

/// voption to option, for List.choose.
let toOption =
    function
    | ValueSome v -> Some v
    | ValueNone -> None

/// The string elements of an array property; empty when the property is missing or null.
let strings name e =
    array name e
    |> List.choose (fun v -> if v.ValueKind = JsonValueKind.String then Some(v.GetString()) else None)

let required name (reader: string -> JsonElement -> 'T voption) e =
    match reader name e with
    | ValueSome v -> v
    | ValueNone -> raise (InvalidJson $"Missing or invalid field '{name}'")

let requiredString name e = required name tryString e

/// A string field holding one of a closed set of codes, read with ofCode.
let requiredCode name (ofCode: string -> 'T) e = requiredString name e |> ofCode
