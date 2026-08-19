namespace Shortlink.Core

open System
open System.Security.Cryptography

/// Generation and validation of short codes / custom slugs.
module ShortCode =

    /// Unambiguous base-62 alphabet used for generated codes.
    let alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"

    let minLength = 4
    let defaultLength = 5
    let maxSlugLength = 255

    /// Characters allowed in a custom slug: url-safe, no percent-encoding needed.
    let private slugChars =
        Set.ofSeq (alphabet + "-_.~+")

    /// Generate a cryptographically random short code of the given length.
    let generate (length: int) : string =
        let length = max minLength length
        let chars = Array.zeroCreate<char> length
        for i in 0 .. length - 1 do
            chars.[i] <- alphabet.[RandomNumberGenerator.GetInt32(alphabet.Length)]
        String(chars)

    /// Validate a caller-supplied custom slug. Slugs may contain slashes to
    /// allow "path-style" short URLs (e.g. "docs/intro"), but no empty segments.
    let validateSlug (slug: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace slug then
            Error "Custom slug cannot be empty."
        else
            let slug = slug.Trim().Trim('/')
            if slug.Length = 0 then
                Error "Custom slug cannot be empty."
            elif slug.Length > maxSlugLength then
                Error $"Custom slug cannot be longer than {maxSlugLength} characters."
            else
                let segments = slug.Split('/')
                let badSegment = segments |> Array.tryFind (fun s -> s.Length = 0)
                match badSegment with
                | Some _ -> Error "Custom slug cannot contain empty path segments."
                | None ->
                    let invalidChar =
                        slug
                        |> Seq.tryFind (fun c -> c <> '/' && not (slugChars.Contains c))
                    match invalidChar with
                    | Some c -> Error $"Custom slug contains an invalid character: '{c}'."
                    | None -> Ok slug
