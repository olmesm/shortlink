namespace Shortlink.Core

open System
open System.Security.Cryptography

/// A short code / custom slug that has passed validation. Construction is
/// private: values come only from `ShortCode.generate` or `ShortCode.ofSlug`.
type ShortCode =
    private
    | ShortCode of string

    member this.Value = let (ShortCode v) = this in v

/// Generation and validation of short codes / custom slugs.
[<RequireQualifiedAccess>]
module ShortCode =

    /// Unambiguous base-62 alphabet used for generated codes.
    let alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"

    let minLength = 4
    let defaultLength = 5
    let maxSlugLength = 255

    /// Characters allowed in a custom slug: url-safe, no percent-encoding needed.
    let private slugChars = Set.ofSeq (alphabet + "-_.~+")

    let value (ShortCode v) = v

    /// Generate a cryptographically random short code of the given length.
    let generate (length: int) : ShortCode =
        let length = max minLength length
        let chars = Array.init length (fun _ -> alphabet.[RandomNumberGenerator.GetInt32(alphabet.Length)])
        ShortCode(String chars)

    /// Parse a caller-supplied custom slug. Slugs may contain slashes to
    /// allow "path-style" short URLs (e.g. "docs/intro"), but no empty segments.
    let ofSlug (slug: string) : Result<ShortCode, string> =
        if String.IsNullOrWhiteSpace slug then
            Error "Custom slug cannot be empty."
        else
            let slug = slug.Trim().Trim('/')

            if slug.Length = 0 then
                Error "Custom slug cannot be empty."
            elif slug.Length > maxSlugLength then
                Error $"Custom slug cannot be longer than {maxSlugLength} characters."
            else
                let hasEmptySegment = slug.Split('/') |> Array.exists (fun s -> s.Length = 0)

                if hasEmptySegment then
                    Error "Custom slug cannot contain empty path segments."
                else
                    match slug |> Seq.tryFind (fun c -> c <> '/' && not (slugChars.Contains c)) with
                    | Some c -> Error $"Custom slug contains an invalid character: '{c}'."
                    | None -> Ok(ShortCode slug)

    /// Would this string be accepted as a slug? Used to classify orphan traffic.
    let isValidSlug (candidate: string) = ofSlug candidate |> Result.isOk
