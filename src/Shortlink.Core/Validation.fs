namespace Shortlink.Core

open System
open FsToolkit.ErrorHandling

/// An absolute http(s) URL that has passed validation.
type LongUrl =
    private
    | LongUrl of string

    member this.Value = let (LongUrl v) = this in v

[<RequireQualifiedAccess>]
module LongUrl =

    let value (LongUrl v) = v

    let create (url: string) : Result<LongUrl, string> =
        if String.IsNullOrWhiteSpace url then
            Error "The long URL is required."
        else
            let url = url.Trim()

            match Uri.TryCreate(url, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps ->
                if url.Length > 2048 then
                    Error "The long URL cannot be longer than 2048 characters."
                else
                    Ok(LongUrl url)
            | _ -> Error "The long URL must be an absolute http(s) URL."

/// A normalized tag name: trimmed, lowercase, comma-free.
type TagName =
    private
    | TagName of string

    member this.Value = let (TagName v) = this in v

[<RequireQualifiedAccess>]
module TagName =

    let value (TagName v) = v

    let create (tag: string) : Result<TagName, string> =
        if String.IsNullOrWhiteSpace tag then
            Error "Tag names cannot be empty."
        else
            let t = tag.Trim().ToLowerInvariant()

            if t.Length > 255 then Error "Tag names cannot be longer than 255 characters."
            elif t.Contains ',' then Error "Tag names cannot contain commas."
            else Ok(TagName t)

    /// Parse many tags, dropping duplicates while preserving first-seen order.
    let createMany (tags: string seq) : Result<TagName list, string> =
        tags
        |> List.ofSeq
        |> List.traverseResultM create
        |> Result.map List.distinct

    /// Split a comma-separated form field into tags.
    let parseCsv (csv: string) : Result<TagName list, string> =
        csv.Split(',')
        |> Array.map (fun t -> t.Trim())
        |> Array.filter (fun t -> t <> "")
        |> createMany

/// A domain authority (host, optionally with port): no scheme, no path.
type DomainAuthority =
    private
    | DomainAuthority of string

    member this.Value = let (DomainAuthority v) = this in v

[<RequireQualifiedAccess>]
module DomainAuthority =

    let value (DomainAuthority v) = v

    let create (authority: string) : Result<DomainAuthority, string> =
        if String.IsNullOrWhiteSpace authority then
            Error "The domain authority is required."
        else
            let a = authority.Trim().ToLowerInvariant()

            if a.Contains "://" || a.Contains "/" then
                Error "The domain must be a plain authority (host or host:port), without scheme or path."
            else
                match Uri.TryCreate($"http://{a}", UriKind.Absolute) with
                | true, uri when uri.Authority = a || uri.Authority = a.TrimEnd(':') -> Ok(DomainAuthority a)
                | _ -> Error "The domain is not a valid authority."

module Validation =

    /// Merge a redirect target with the incoming request's query string, when
    /// query forwarding is enabled. Params already present in the target are
    /// kept; incoming params are appended. Operates on the resolved target
    /// (which may come from a redirect rule), hence plain strings.
    let forwardQuery (targetUrl: string) (incoming: (string * string) list) : string =
        match incoming with
        | [] -> targetUrl
        | pairs ->
            let encoded =
                pairs
                |> List.map (fun (k, v) ->
                    if String.IsNullOrEmpty v then Uri.EscapeDataString k
                    else $"{Uri.EscapeDataString k}={Uri.EscapeDataString v}")
                |> String.concat "&"

            let hashIdx = targetUrl.IndexOf '#'

            let base', fragment =
                if hashIdx >= 0 then targetUrl.Substring(0, hashIdx), targetUrl.Substring(hashIdx)
                else targetUrl, ""

            let sep = if base'.Contains "?" then "&" else "?"
            base' + sep + encoded + fragment
