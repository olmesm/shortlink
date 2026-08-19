namespace Shortlink.Core

open System

/// Evaluation of conditional redirect rules against an incoming request.
module RedirectRules =

    /// Lightweight device detection from a user-agent string; only needs to be
    /// accurate enough to drive device-condition redirect rules.
    let detectDevice (userAgent: string option) : Device =
        match userAgent with
        | None -> Device.Desktop
        | Some ua ->
            let ua = ua.ToLowerInvariant()
            if ua.Contains "android" then Device.Android
            elif ua.Contains "iphone" || ua.Contains "ipad" || ua.Contains "ipod" then Device.Ios
            elif ua.Contains "mobile" then Device.Mobile
            else Device.Desktop

    /// Does the Accept-Language header include the wanted language?
    /// Matches on the primary subtag: wanting "en" matches "en-GB"; wanting
    /// "en-US" requires "en-US" (case-insensitive).
    let matchesLanguage (wanted: string) (acceptLanguage: string option) : bool =
        match acceptLanguage with
        | None -> false
        | Some header ->
            let wanted = wanted.Trim().ToLowerInvariant()
            header.Split(',')
            |> Array.map (fun part -> part.Split(';').[0].Trim().ToLowerInvariant())
            |> Array.filter (fun lang -> lang <> "" && lang <> "*")
            |> Array.exists (fun lang ->
                lang = wanted
                || (not (wanted.Contains "-") && lang.Split('-').[0] = wanted))

    let private matchesCondition (visitor: VisitorContext) (condition: RuleCondition) : bool =
        match condition with
        | DeviceIs wanted ->
            let device = detectDevice visitor.UserAgent
            match wanted, device with
            | Device.Mobile, (Device.Android | Device.Ios | Device.Mobile) -> true
            | w, d -> w = d
        | LanguageIs lang -> matchesLanguage lang visitor.AcceptLanguage
        | QueryParamIs(key, value) ->
            match visitor.Query.TryFind key with
            | Some v -> String.Equals(v, value, StringComparison.Ordinal)
            | None -> false
        | IpInRange cidr ->
            match visitor.RemoteIp with
            | Some ip -> Anonymize.ipInCidr cidr ip
            | None -> false

    /// Resolve the target long URL for a visit: the first rule (by priority)
    /// whose conditions all match wins; otherwise the default long URL.
    let resolveTarget (defaultLongUrl: string) (rules: RedirectRule list) (visitor: VisitorContext) : string =
        rules
        |> List.sortBy (fun r -> r.Priority)
        |> List.tryFind (fun rule ->
            not rule.Conditions.IsEmpty
            && rule.Conditions |> List.forall (matchesCondition visitor))
        |> Option.map (fun rule -> rule.LongUrl)
        |> Option.defaultValue defaultLongUrl
