namespace Shortlink.Core

open System

/// HTTP status used when redirecting a short URL to its long URL.
type RedirectStatus =
    | MovedPermanently
    | Found
    | TemporaryRedirect
    | PermanentRedirect

    member this.Code =
        match this with
        | MovedPermanently -> 301
        | Found -> 302
        | TemporaryRedirect -> 307
        | PermanentRedirect -> 308

    static member OfCode(code: int) =
        match code with
        | 301 -> Some MovedPermanently
        | 302 -> Some Found
        | 307 -> Some TemporaryRedirect
        | 308 -> Some PermanentRedirect
        | _ -> None

/// Device family detected from a visitor's user agent, used by redirect rules.
type Device =
    | Android
    | Ios
    | Desktop
    | Mobile

    member this.Slug =
        match this with
        | Android -> "android"
        | Ios -> "ios"
        | Desktop -> "desktop"
        | Mobile -> "mobile"

    static member OfSlug(s: string) =
        match s with
        | null -> None
        | s ->
            match s.Trim().ToLowerInvariant() with
            | "android" -> Some Android
            | "ios" -> Some Ios
            | "desktop" -> Some Desktop
            | "mobile" -> Some Mobile
            | _ -> None

/// A single condition inside a redirect rule. All conditions of a rule must
/// match for the rule's target URL to be used.
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
type VisitType =
    | ValidShortUrl
    | OrphanBaseUrl
    | OrphanInvalidShortUrl
    | OrphanRegular404

    member this.Slug =
        match this with
        | ValidShortUrl -> "valid"
        | OrphanBaseUrl -> "base_url"
        | OrphanInvalidShortUrl -> "invalid_short_url"
        | OrphanRegular404 -> "regular_404"

    member this.IsOrphan =
        match this with
        | ValidShortUrl -> false
        | _ -> true

    static member OfSlug(s: string) =
        match s with
        | "valid" -> Some ValidShortUrl
        | "base_url" -> Some OrphanBaseUrl
        | "invalid_short_url" -> Some OrphanInvalidShortUrl
        | "regular_404" -> Some OrphanRegular404
        | _ -> None

/// Access level attached to an API key.
type ApiKeyRole =
    | AdminKey
    | AuthorKey
    | DomainKey of domainId: int64

/// Dashboard user role.
type UserRole =
    | AdminUser
    | RegularUser

    member this.Slug =
        match this with
        | AdminUser -> "admin"
        | RegularUser -> "user"

    static member OfSlug(s: string) =
        match s with
        | "admin" -> Some AdminUser
        | "user" -> Some RegularUser
        | _ -> None

/// Events that webhooks can subscribe to.
type WebhookEvent =
    | UrlCreated
    | VisitRecorded
    | OrphanVisitRecorded

    member this.Slug =
        match this with
        | UrlCreated -> "url.created"
        | VisitRecorded -> "visit.recorded"
        | OrphanVisitRecorded -> "orphan_visit.recorded"

    static member OfSlug(s: string) =
        match s with
        | "url.created" -> Some UrlCreated
        | "visit.recorded" -> Some VisitRecorded
        | "orphan_visit.recorded" -> Some OrphanVisitRecorded
        | _ -> None

    static member All = [ UrlCreated; VisitRecorded; OrphanVisitRecorded ]

/// Why a short URL, although it exists, refuses to redirect right now.
type ExpirationReason =
    | NotYetValid
    | NoLongerValid
    | MaxVisitsReached

module DomainErrors =

    type CreateShortUrlError =
        | InvalidLongUrl of string
        | InvalidSlug of string
        | SlugInUse of slug: string * domain: string
        | UnknownDomain of string
        | CodeGenerationExhausted

    type EditShortUrlError =
        | ShortUrlNotFound of code: string * domain: string option
        | EditInvalidUrl of string
