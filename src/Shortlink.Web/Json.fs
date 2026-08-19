namespace Shortlink.Web

open System.Text.Json
open System.Text.Json.Serialization
open Falco

module Json =

    /// Shared serializer options: camelCase, F# options as null-or-value,
    /// enums as strings, permissive number handling.
    let options =
        let o =
            JsonSerializerOptions(
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                NumberHandling = JsonNumberHandling.AllowReadingFromString)
        JsonFSharpOptions
            .Default()
            .WithAllowNullFields(true)
            .WithSkippableOptionFields(true)
            .AddToJsonSerializerOptions(o)
        o

    let serialize (value: 'T) = JsonSerializer.Serialize(value, options)

    let deserialize<'T> (json: string) = JsonSerializer.Deserialize<'T>(json, options)

    /// Respond with JSON using the shared options.
    let respond (value: 'T) : HttpHandler = Response.ofJsonOptions options value
