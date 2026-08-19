module Shortlink.Tests.ShortCodeTests

open Xunit
open Shortlink.Core

[<Fact>]
let ``generated codes have the requested length`` () =
    Assert.Equal(5, (ShortCode.generate 5).Length)
    Assert.Equal(12, (ShortCode.generate 12).Length)

[<Fact>]
let ``generated codes never go below the minimum length`` () =
    Assert.Equal(ShortCode.minLength, (ShortCode.generate 1).Length)

[<Fact>]
let ``generated codes only use the alphabet`` () =
    for _ in 1..50 do
        let code = ShortCode.generate 8
        Assert.All(code, fun c -> Assert.Contains(c, ShortCode.alphabet))

[<Fact>]
let ``generated codes are (overwhelmingly) unique`` () =
    let codes = [ for _ in 1..1000 -> ShortCode.generate 8 ]
    Assert.Equal(1000, (List.distinct codes).Length)

[<Theory>]
[<InlineData("my-slug")>]
[<InlineData("MySlug_2024")>]
[<InlineData("docs/intro")>]
[<InlineData("a.b~c+d")>]
let ``valid slugs are accepted`` (slug: string) =
    match ShortCode.validateSlug slug with
    | Ok _ -> ()
    | Error e -> failwith e

[<Theory>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("has space")>]
[<InlineData("emoji😀")>]
[<InlineData("a//b")>]
[<InlineData("per%cent")>]
let ``invalid slugs are rejected`` (slug: string) =
    match ShortCode.validateSlug slug with
    | Ok s -> failwith $"expected rejection, got '{s}'"
    | Error _ -> ()

[<Fact>]
let ``slugs are trimmed of surrounding slashes`` () =
    Assert.Equal(Ok "abc", ShortCode.validateSlug "/abc/")
