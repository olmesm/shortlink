module Shortlink.Tests.RedirectRulesTests

open Xunit
open Shortlink.Core

let private visitor ua lang query ip : VisitorContext =
    { UserAgent = ua
      AcceptLanguage = lang
      Query = Map.ofList query
      RemoteIp = ip }

[<Theory>]
[<InlineData("Mozilla/5.0 (Linux; Android 14)", "android")>]
[<InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0)", "ios")>]
[<InlineData("Mozilla/5.0 (iPad; CPU OS 17_0)", "ios")>]
[<InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X)", "desktop")>]
[<InlineData("Mozilla/5.0 (Mobile; rv:1.0)", "mobile")>]
let ``device detection`` (ua: string) (expected: string) =
    Assert.Equal(expected, (RedirectRules.detectDevice (Some ua)).Slug)

[<Fact>]
let ``missing user agent counts as desktop`` () =
    Assert.Equal(Device.Desktop, RedirectRules.detectDevice None)

[<Theory>]
[<InlineData("en", "en-GB,en;q=0.9", true)>]
[<InlineData("en-GB", "en-GB,en;q=0.9", true)>]
[<InlineData("en-US", "en-GB,en;q=0.9", false)>]
[<InlineData("fr", "en-GB,en;q=0.9", false)>]
[<InlineData("EN", "en;q=0.5", true)>]
let ``language matching`` (wanted: string) (header: string) (expected: bool) =
    Assert.Equal(expected, RedirectRules.matchesLanguage wanted (Some header))

[<Fact>]
let ``first matching rule by priority wins`` () =
    let rules =
        [ { Priority = 2
            LongUrl = "https://example.com/second"
            Conditions = [ DeviceIs Device.Android ] }
          { Priority = 1
            LongUrl = "https://example.com/first"
            Conditions = [ DeviceIs Device.Android ] } ]
    let v = visitor (Some "Android phone") None [] None
    Assert.Equal("https://example.com/first", RedirectRules.resolveTarget "https://example.com/default" rules v)

[<Fact>]
let ``all conditions of a rule must match`` () =
    let rules =
        [ { Priority = 1
            LongUrl = "https://example.com/match"
            Conditions = [ DeviceIs Device.Android; QueryParamIs("src", "mail") ] } ]
    let androidNoParam = visitor (Some "Android") None [] None
    let androidWithParam = visitor (Some "Android") None [ "src", "mail" ] None
    Assert.Equal("https://example.com/default", RedirectRules.resolveTarget "https://example.com/default" rules androidNoParam)
    Assert.Equal("https://example.com/match", RedirectRules.resolveTarget "https://example.com/default" rules androidWithParam)

[<Fact>]
let ``mobile matches android and ios`` () =
    let rules =
        [ { Priority = 1
            LongUrl = "https://example.com/mobile"
            Conditions = [ DeviceIs Device.Mobile ] } ]
    let android = visitor (Some "Android") None [] None
    let iphone = visitor (Some "iPhone") None [] None
    let desktop = visitor (Some "Macintosh") None [] None
    Assert.Equal("https://example.com/mobile", RedirectRules.resolveTarget "d" rules android)
    Assert.Equal("https://example.com/mobile", RedirectRules.resolveTarget "d" rules iphone)
    Assert.Equal("d", RedirectRules.resolveTarget "d" rules desktop)

[<Fact>]
let ``ip range condition matches visitor ip`` () =
    let rules =
        [ { Priority = 1
            LongUrl = "https://example.com/internal"
            Conditions = [ IpInRange "10.0.0.0/8" ] } ]
    let internalVisitor = visitor None None [] (Some "10.2.3.4")
    let externalVisitor = visitor None None [] (Some "8.8.8.8")
    Assert.Equal("https://example.com/internal", RedirectRules.resolveTarget "d" rules internalVisitor)
    Assert.Equal("d", RedirectRules.resolveTarget "d" rules externalVisitor)

[<Fact>]
let ``rules without conditions never fire`` () =
    let rules =
        [ { Priority = 1
            LongUrl = "https://example.com/never"
            Conditions = [] } ]
    Assert.Equal("d", RedirectRules.resolveTarget "d" rules (visitor None None [] None))
