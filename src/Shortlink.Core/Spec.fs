namespace Shortlink.Core

open System
open FsToolkit.ErrorHandling

/// When a short URL is allowed to redirect: an optional validity window plus
/// an optional visit budget. Invariants (`MaxVisits > 0`, `ValidSince <
/// ValidUntil`) are enforced by `Lifetime.create` — build values through it.
type Lifetime =
    { ValidSince: DateTime option
      ValidUntil: DateTime option
      MaxVisits: int64 option }

[<RequireQualifiedAccess>]
module Lifetime =

    let unbounded =
        { ValidSince = None
          ValidUntil = None
          MaxVisits = None }

    let create (validSince: DateTime option) (validUntil: DateTime option) (maxVisits: int64 option) : Result<Lifetime, string> =
        match maxVisits with
        | Some n when n <= 0L -> Error "maxVisits must be greater than zero."
        | _ ->
            match validSince, validUntil with
            | Some since, Some until when since >= until ->
                Error "validSince must be earlier than validUntil."
            | _ ->
                Ok
                    { ValidSince = validSince
                      ValidUntil = validUntil
                      MaxVisits = maxVisits }

    /// Is the short URL currently allowed to redirect?
    let checkActive (now: DateTime) (validVisitCount: int64) (lifetime: Lifetime) : Result<unit, ExpirationReason> =
        match lifetime.ValidSince with
        | Some since when now < since -> Error ExpirationReason.NotYetValid
        | _ ->
            match lifetime.ValidUntil with
            | Some until when now > until -> Error ExpirationReason.NoLongerValid
            | _ ->
                match lifetime.MaxVisits with
                | Some maxV when validVisitCount >= maxV -> Error ExpirationReason.MaxVisitsReached
                | _ -> Ok()

/// A fully validated request to create a short URL. This is the single
/// sanctioned construction path — the REST API, the dashboard and any future
/// entry point all go through `ShortUrlSpec.create`, so every invariant is
/// enforced exactly once.
type ShortUrlSpec =
    { LongUrl: LongUrl
      CustomSlug: ShortCode option
      CodeLength: int option
      Domain: DomainAuthority option
      Title: string option
      Tags: TagName list
      Lifetime: Lifetime
      RedirectStatus: RedirectStatus option
      ForwardQuery: bool option
      Crawlable: bool option
      FindIfExists: bool }

[<RequireQualifiedAccess>]
module ShortUrlSpec =

    /// Raw, unvalidated creation input as it arrives from a JSON body or form.
    type Input =
        { LongUrl: string
          CustomSlug: string option
          CodeLength: int option
          Domain: string option
          Title: string option
          Tags: string list
          ValidSince: DateTime option
          ValidUntil: DateTime option
          MaxVisits: int64 option
          RedirectStatus: int option
          ForwardQuery: bool option
          Crawlable: bool option
          FindIfExists: bool }

    let input longUrl : Input =
        { LongUrl = longUrl
          CustomSlug = None
          CodeLength = None
          Domain = None
          Title = None
          Tags = []
          ValidSince = None
          ValidUntil = None
          MaxVisits = None
          RedirectStatus = None
          ForwardQuery = None
          Crawlable = None
          FindIfExists = false }

    let private parseStatus (code: int option) : Result<RedirectStatus option, ShortUrlError> =
        match code with
        | None -> Ok None
        | Some code ->
            RedirectStatus.OfCode code
            |> Option.map Some
            |> Result.requireSome (ShortUrlError.InvalidRedirectStatus code)

    /// Parse and validate raw input into a spec.
    let create (input: Input) : Result<ShortUrlSpec, ShortUrlError> =
        result {
            let! longUrl = LongUrl.create input.LongUrl |> Result.mapError ShortUrlError.InvalidLongUrl

            let! customSlug =
                input.CustomSlug
                |> Option.traverseResult ShortCode.ofSlug
                |> Result.mapError ShortUrlError.InvalidSlug

            let! domain =
                input.Domain
                |> Option.traverseResult DomainAuthority.create
                |> Result.mapError ShortUrlError.UnknownDomain

            let! tags = TagName.createMany input.Tags |> Result.mapError ShortUrlError.InvalidTag

            let! lifetime =
                Lifetime.create input.ValidSince input.ValidUntil input.MaxVisits
                |> Result.mapError ShortUrlError.InvalidLifetime

            let! redirectStatus = parseStatus input.RedirectStatus

            return
                { LongUrl = longUrl
                  CustomSlug = customSlug
                  CodeLength = input.CodeLength
                  Domain = domain
                  Title = input.Title |> Option.map (fun t -> t.Trim()) |> Option.filter (fun t -> t <> "")
                  Tags = tags
                  Lifetime = lifetime
                  RedirectStatus = redirectStatus
                  ForwardQuery = input.ForwardQuery
                  Crawlable = input.Crawlable
                  FindIfExists = input.FindIfExists }
        }

/// A fully validated edit: the final values every mutable field should take.
/// PATCH-merging (absent = keep current) happens *before* validation, so the
/// resulting state is checked as a whole.
type ShortUrlEdit =
    { LongUrl: LongUrl
      Title: string option
      Lifetime: Lifetime
      RedirectStatus: RedirectStatus
      ForwardQuery: bool
      Crawlable: bool
      /// None = leave tags unchanged.
      Tags: TagName list option }

[<RequireQualifiedAccess>]
module ShortUrlEdit =

    type Input =
        { LongUrl: string
          Title: string option
          ValidSince: DateTime option
          ValidUntil: DateTime option
          MaxVisits: int64 option
          RedirectStatus: int
          ForwardQuery: bool
          Crawlable: bool
          Tags: string list option }

    let create (input: Input) : Result<ShortUrlEdit, ShortUrlError> =
        result {
            let! longUrl = LongUrl.create input.LongUrl |> Result.mapError ShortUrlError.InvalidLongUrl

            let! lifetime =
                Lifetime.create input.ValidSince input.ValidUntil input.MaxVisits
                |> Result.mapError ShortUrlError.InvalidLifetime

            let! redirectStatus =
                RedirectStatus.OfCode input.RedirectStatus
                |> Result.requireSome (ShortUrlError.InvalidRedirectStatus input.RedirectStatus)

            let! tags =
                input.Tags
                |> Option.traverseResult TagName.createMany
                |> Result.mapError ShortUrlError.InvalidTag

            return
                { LongUrl = longUrl
                  Title = input.Title |> Option.map (fun t -> t.Trim()) |> Option.filter (fun t -> t <> "")
                  Lifetime = lifetime
                  RedirectStatus = redirectStatus
                  ForwardQuery = input.ForwardQuery
                  Crawlable = input.Crawlable
                  Tags = tags }
        }
