namespace Shortlink.Core

open System

module Validation =

    /// Validate a long URL: absolute http/https URI.
    let validateLongUrl (url: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace url then
            Error "The long URL is required."
        else
            let url = url.Trim()
            match Uri.TryCreate(url, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps ->
                if url.Length > 2048 then Error "The long URL cannot be longer than 2048 characters."
                else Ok url
            | _ -> Error "The long URL must be an absolute http(s) URL."

    /// Validate a tag name; tags are normalized to lowercase, trimmed.
    let normalizeTag (tag: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace tag then
            Error "Tag names cannot be empty."
        else
            let t = tag.Trim().ToLowerInvariant()
            if t.Length > 255 then Error "Tag names cannot be longer than 255 characters."
            elif t.Contains ',' then Error "Tag names cannot contain commas."
            else Ok t

    let normalizeTags (tags: string seq) : Result<string list, string> =
        (Ok [], tags)
        ||> Seq.fold (fun acc tag ->
            match acc, normalizeTag tag with
            | Error e, _ -> Error e
            | _, Error e -> Error e
            | Ok list, Ok t -> Ok(if List.contains t list then list else list @ [ t ]))

    /// Validate a domain authority (host, optionally with port). Rejects schemes and paths.
    let validateDomainAuthority (authority: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace authority then
            Error "The domain authority is required."
        else
            let a = authority.Trim().ToLowerInvariant()
            if a.Contains "://" || a.Contains "/" then
                Error "The domain must be a plain authority (host or host:port), without scheme or path."
            else
                match Uri.TryCreate($"http://{a}", UriKind.Absolute) with
                | true, uri when uri.Authority = a || uri.Authority = a.TrimEnd(':') -> Ok a
                | _ -> Error "The domain is not a valid authority."

    /// Merge a short URL's configured long URL with the incoming request's query
    /// string, when query forwarding is enabled. Params already present in the
    /// long URL are kept; incoming params are appended.
    let forwardQuery (longUrl: string) (incoming: (string * string) list) : string =
        match incoming with
        | [] -> longUrl
        | pairs ->
            let encoded =
                pairs
                |> List.map (fun (k, v) ->
                    if String.IsNullOrEmpty v then Uri.EscapeDataString k
                    else $"{Uri.EscapeDataString k}={Uri.EscapeDataString v}")
                |> String.concat "&"
            let hashIdx = longUrl.IndexOf '#'
            let base', fragment =
                if hashIdx >= 0 then longUrl.Substring(0, hashIdx), longUrl.Substring(hashIdx)
                else longUrl, ""
            let sep = if base'.Contains "?" then "&" else "?"
            base' + sep + encoded + fragment
