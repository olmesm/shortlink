module Shortlink.Tests.ValidationTests

open Xunit
open Shortlink.Core

[<Theory>]
[<InlineData("https://example.com")>]
[<InlineData("http://example.com/path?q=1#frag")>]
let ``valid long URLs are accepted`` (url: string) =
    match Validation.validateLongUrl url with
    | Ok _ -> ()
    | Error e -> failwith e

[<Theory>]
[<InlineData("")>]
[<InlineData("notaurl")>]
[<InlineData("ftp://example.com/file")>]
[<InlineData("javascript:alert(1)")>]
let ``invalid long URLs are rejected`` (url: string) =
    match Validation.validateLongUrl url with
    | Ok u -> failwith $"expected rejection, got '{u}'"
    | Error _ -> ()

[<Fact>]
let ``tags are normalized to lowercase and deduplicated`` () =
    Assert.Equal(Ok [ "alpha"; "beta" ], Validation.normalizeTags [ " Alpha "; "beta"; "ALPHA" ])

[<Fact>]
let ``tags with commas are rejected`` () =
    match Validation.normalizeTag "a,b" with
    | Ok _ -> failwith "expected rejection"
    | Error _ -> ()

[<Theory>]
[<InlineData("example.com")>]
[<InlineData("links.example.com:8443")>]
let ``valid domain authorities are accepted`` (authority: string) =
    match Validation.validateDomainAuthority authority with
    | Ok _ -> ()
    | Error e -> failwith e

[<Theory>]
[<InlineData("https://example.com")>]
[<InlineData("example.com/path")>]
[<InlineData("")>]
let ``invalid domain authorities are rejected`` (authority: string) =
    match Validation.validateDomainAuthority authority with
    | Ok a -> failwith $"expected rejection, got '{a}'"
    | Error _ -> ()

[<Fact>]
let ``query forwarding appends params`` () =
    Assert.Equal(
        "https://example.com/p?a=1",
        Validation.forwardQuery "https://example.com/p" [ "a", "1" ])

[<Fact>]
let ``query forwarding merges with existing query`` () =
    Assert.Equal(
        "https://example.com/p?x=0&a=1&b=2",
        Validation.forwardQuery "https://example.com/p?x=0" [ "a", "1"; "b", "2" ])

[<Fact>]
let ``query forwarding keeps the fragment last`` () =
    Assert.Equal(
        "https://example.com/p?a=1#sec",
        Validation.forwardQuery "https://example.com/p#sec" [ "a", "1" ])

[<Fact>]
let ``query forwarding url-encodes values`` () =
    Assert.Equal(
        "https://example.com/p?q=a%20b%26c",
        Validation.forwardQuery "https://example.com/p" [ "q", "a b&c" ])

[<Fact>]
let ``query forwarding with no params is identity`` () =
    Assert.Equal("https://example.com/p", Validation.forwardQuery "https://example.com/p" [])
