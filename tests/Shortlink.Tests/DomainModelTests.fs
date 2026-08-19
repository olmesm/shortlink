module Shortlink.Tests.DomainModelTests

open System
open Xunit
open Shortlink.Core

// ---- Lifetime invariants ----

[<Fact>]
let ``lifetimes reject a non-positive max visit budget`` () =
    match Lifetime.create None None (Some 0L) with
    | Ok _ -> failwith "expected rejection"
    | Error e -> Assert.Contains("greater than zero", e)

    match Lifetime.create None None (Some -5L) with
    | Ok _ -> failwith "expected rejection"
    | Error _ -> ()

[<Fact>]
let ``lifetimes reject an inverted validity window`` () =
    let since = DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
    let until = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)

    match Lifetime.create (Some since) (Some until) None with
    | Ok _ -> failwith "expected rejection"
    | Error e -> Assert.Contains("earlier than", e)

[<Fact>]
let ``lifetime activity checks cover all three expiry reasons`` () =
    let now = DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc)

    let ok = Lifetime.checkActive now 0L Lifetime.unbounded
    Assert.Equal(Ok(), ok)

    let notYet =
        Lifetime.checkActive now 0L { Lifetime.unbounded with ValidSince = Some(now.AddDays 1.0) }

    Assert.Equal(Error ExpirationReason.NotYetValid, notYet)

    let expired =
        Lifetime.checkActive now 0L { Lifetime.unbounded with ValidUntil = Some(now.AddDays -1.0) }

    Assert.Equal(Error ExpirationReason.NoLongerValid, expired)

    let exhausted =
        Lifetime.checkActive now 5L { Lifetime.unbounded with MaxVisits = Some 5L }

    Assert.Equal(Error ExpirationReason.MaxVisitsReached, exhausted)

// ---- ShortUrlSpec: the single validation path ----

[<Fact>]
let ``specs collect every validated piece`` () =
    let input =
        { ShortUrlSpec.input "https://example.com/x" with
            CustomSlug = Some "My-Slug"
            Tags = [ " Marketing "; "LAUNCH" ]
            MaxVisits = Some 10L
            RedirectStatus = Some 301 }

    match ShortUrlSpec.create input with
    | Error e -> failwith $"unexpected: {e.Message}"
    | Ok spec ->
        Assert.Equal("https://example.com/x", spec.LongUrl.Value)
        Assert.Equal(Some "My-Slug", spec.CustomSlug |> Option.map (fun c -> c.Value))
        Assert.Equal<string list>([ "marketing"; "launch" ], spec.Tags |> List.map TagName.value)
        Assert.Equal(Some RedirectStatus.MovedPermanently, spec.RedirectStatus)

[<Fact>]
let ``specs reject a zero max visit budget`` () =
    match ShortUrlSpec.create { ShortUrlSpec.input "https://example.com" with MaxVisits = Some 0L } with
    | Ok _ -> failwith "expected rejection"
    | Error(ShortUrlError.InvalidLifetime _) -> ()
    | Error other -> failwith $"wrong error: {other}"

[<Fact>]
let ``specs reject unsupported redirect statuses`` () =
    match ShortUrlSpec.create { ShortUrlSpec.input "https://example.com" with RedirectStatus = Some 418 } with
    | Ok _ -> failwith "expected rejection"
    | Error(ShortUrlError.InvalidRedirectStatus 418) -> ()
    | Error other -> failwith $"wrong error: {other}"

[<Fact>]
let ``specs blank out whitespace titles`` () =
    match ShortUrlSpec.create { ShortUrlSpec.input "https://example.com" with Title = Some "   " } with
    | Ok spec -> Assert.Equal(None, spec.Title)
    | Error e -> failwith e.Message

// ---- API key roles: unknown stored roles must never default to admin ----

[<Fact>]
let ``api key role parsing is fail-closed`` () =
    Assert.Equal(Some ApiKeyRole.Admin, ApiKeyRole.OfStored("admin", None))
    Assert.Equal(Some ApiKeyRole.Author, ApiKeyRole.OfStored("author", None))
    Assert.Equal(Some(ApiKeyRole.Domain(DomainId 7L)), ApiKeyRole.OfStored("domain", Some 7L))
    // A domain role without a domain id is corrupt, not admin.
    Assert.Equal(None, ApiKeyRole.OfStored("domain", None))
    // Unknown roles are rejected, not defaulted.
    Assert.Equal(None, ApiKeyRole.OfStored("superuser", None))
    Assert.Equal(None, ApiKeyRole.OfStored("", None))

[<Fact>]
let ``typed ids do not cross-assign`` () =
    // Compile-time guarantee — this test documents the intent.
    let shortUrlId = ShortUrlId 1L
    let domainId = DomainId 1L
    Assert.Equal(1L, shortUrlId.Value)
    Assert.Equal(1L, domainId.Value)
