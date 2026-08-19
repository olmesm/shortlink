namespace Shortlink.Core

open System

/// HTTP status used when redirecting a short URL to its long URL.
[<RequireQualifiedAccess>]
type RedirectStatus =
    | MovedPermanently
    | Found
    | TemporaryRedirect
    | PermanentRedirect

    member this.Code =
        match this with
        | RedirectStatus.MovedPermanently -> 301
        | RedirectStatus.Found -> 302
        | RedirectStatus.TemporaryRedirect -> 307
        | RedirectStatus.PermanentRedirect -> 308

    static member OfCode(code: int) =
        match code with
        | 301 -> Some RedirectStatus.MovedPermanently
        | 302 -> Some RedirectStatus.Found
        | 307 -> Some RedirectStatus.TemporaryRedirect
        | 308 -> Some RedirectStatus.PermanentRedirect
        | _ -> None

/// Device family detected from a visitor's user agent, used by redirect rules.
[<RequireQualifiedAccess>]
type Device =
    | Android
    | Ios
    | Desktop
    | Mobile

    member this.Slug =
        match this with
        | Device.Android -> "android"
        | Device.Ios -> "ios"
        | Device.Desktop -> "desktop"
        | Device.Mobile -> "mobile"

    static member OfSlug(s: string) =
        match s with
        | null -> None
        | s ->
            match s.Trim().ToLowerInvariant() with
            | "android" -> Some Device.Android
            | "ios" -> Some Device.Ios
            | "desktop" -> Some Device.Desktop
            | "mobile" -> Some Device.Mobile
            | _ -> None

/// A single condition inside a redirect rule. All conditions of a rule must
/// match for the rule's target URL to be used. Case names are descriptive
/// enough to stay unqualified.
type RuleCondition =
    | DeviceIs of Device
    | LanguageIs of string
    | QueryParamIs of key: string * value: string
    | IpInRange of cidr: string

/// A conditional redirect target attached to a short URL. Rules are evaluated
/// in ascending priority order; the first rule whose conditions all match wins.
type RedirectRule =
    { Priority: int
      LongUrl: string
      Conditions: RuleCondition list }

/// Everything known about the incoming request that redirect rules can match on.
type VisitorContext =
    { UserAgent: string option
      AcceptLanguage: string option
      Query: Map<string, string>
      RemoteIp: string option }

/// Kind of visit being tracked. Anything except ValidShortUrl is an "orphan"
/// visit: traffic that reached the service but not an active short URL.
[<RequireQualifiedAccess>]
type VisitType =
    | ValidShortUrl
    | OrphanBaseUrl
    | OrphanInvalidShortUrl
    | OrphanRegular404

    member this.Slug =
        match this with
        | VisitType.ValidShortUrl -> "valid"
        | VisitType.OrphanBaseUrl -> "base_url"
        | VisitType.OrphanInvalidShortUrl -> "invalid_short_url"
        | VisitType.OrphanRegular404 -> "regular_404"

    member this.IsOrphan =
        match this with
        | VisitType.ValidShortUrl -> false
        | _ -> true

    static member OfSlug(s: string) =
        match s with
        | "valid" -> Some VisitType.ValidShortUrl
        | "base_url" -> Some VisitType.OrphanBaseUrl
        | "invalid_short_url" -> Some VisitType.OrphanInvalidShortUrl
        | "regular_404" -> Some VisitType.OrphanRegular404
        | _ -> None

/// Access level attached to an API key. Parsing is partial on purpose: an
/// unrecognized stored role must be rejected, never defaulted.
[<RequireQualifiedAccess>]
type ApiKeyRole =
    | Admin
    | Author
    | Domain of DomainId

    member this.Slug =
        match this with
        | ApiKeyRole.Admin -> "admin"
        | ApiKeyRole.Author -> "author"
        | ApiKeyRole.Domain _ -> "domain"

    /// Reconstruct a role from its stored representation. Returns None for
    /// unknown role strings and for a domain role missing its domain id.
    static member OfStored(slug: string, domainId: int64 option) =
        match slug, domainId with
        | "admin", _ -> Some ApiKeyRole.Admin
        | "author", _ -> Some ApiKeyRole.Author
        | "domain", Some id -> Some(ApiKeyRole.Domain(DomainId id))
        | _ -> None

/// Dashboard user role.
[<RequireQualifiedAccess>]
type UserRole =
    | Admin
    | Regular

    member this.Slug =
        match this with
        | UserRole.Admin -> "admin"
        | UserRole.Regular -> "user"

    static member OfSlug(s: string) =
        match s with
        | "admin" -> Some UserRole.Admin
        | "user" -> Some UserRole.Regular
        | _ -> None

/// Events that webhooks can subscribe to.
[<RequireQualifiedAccess>]
type WebhookEvent =
    | UrlCreated
    | VisitRecorded
    | OrphanVisitRecorded

    member this.Slug =
        match this with
        | WebhookEvent.UrlCreated -> "url.created"
        | WebhookEvent.VisitRecorded -> "visit.recorded"
        | WebhookEvent.OrphanVisitRecorded -> "orphan_visit.recorded"

    static member OfSlug(s: string) =
        match s with
        | "url.created" -> Some WebhookEvent.UrlCreated
        | "visit.recorded" -> Some WebhookEvent.VisitRecorded
        | "orphan_visit.recorded" -> Some WebhookEvent.OrphanVisitRecorded
        | _ -> None

    static member All =
        [ WebhookEvent.UrlCreated; WebhookEvent.VisitRecorded; WebhookEvent.OrphanVisitRecorded ]

/// Why a short URL, although it exists, refuses to redirect right now.
[<RequireQualifiedAccess>]
type ExpirationReason =
    | NotYetValid
    | NoLongerValid
    | MaxVisitsReached

/// Everything that can go wrong creating (or editing) a short URL.
[<RequireQualifiedAccess>]
type ShortUrlError =
    | InvalidLongUrl of string
    | InvalidSlug of string
    | InvalidTag of string
    | InvalidLifetime of string
    | InvalidRedirectStatus of int
    | SlugInUse of slug: string * domain: string
    | UnknownDomain of string
    | CodeGenerationExhausted

    /// Human-readable message, for UI banners and problem details.
    member this.Message =
        match this with
        | ShortUrlError.InvalidLongUrl m
        | ShortUrlError.InvalidSlug m
        | ShortUrlError.InvalidTag m
        | ShortUrlError.InvalidLifetime m -> m
        | ShortUrlError.InvalidRedirectStatus code ->
            $"'{code}' is not a supported redirect status. Use 301, 302, 307 or 308."
        | ShortUrlError.SlugInUse(slug, domain) ->
            $"The slug '{slug}' is already in use on domain '{domain}'."
        | ShortUrlError.UnknownDomain m -> m
        | ShortUrlError.CodeGenerationExhausted ->
            "Could not find a free short code; try again or use a custom slug."
