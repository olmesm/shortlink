module Shortlink.Tests.AnonymizeTests

open Xunit
open Shortlink.Core

[<Fact>]
let ``ipv4 addresses lose their last octet`` () =
    Assert.Equal(Some "192.168.1.0", Anonymize.anonymizeIp "192.168.1.42")

[<Fact>]
let ``ipv6 addresses are truncated to their /48 prefix`` () =
    Assert.Equal(Some "2001:db8:1::", Anonymize.anonymizeIp "2001:db8:1:2:3:4:5:6")

[<Fact>]
let ``garbage input yields None`` () =
    Assert.Equal(None, Anonymize.anonymizeIp "not-an-ip")

[<Theory>]
[<InlineData("10.0.0.0/8", "10.1.2.3", true)>]
[<InlineData("10.0.0.0/8", "11.0.0.1", false)>]
[<InlineData("192.168.1.0/24", "192.168.1.200", true)>]
[<InlineData("192.168.1.0/24", "192.168.2.1", false)>]
[<InlineData("192.168.1.128/25", "192.168.1.129", true)>]
[<InlineData("192.168.1.128/25", "192.168.1.1", false)>]
[<InlineData("192.168.1.5", "192.168.1.5", true)>]
[<InlineData("192.168.1.5", "192.168.1.6", false)>]
[<InlineData("2001:db8::/32", "2001:db8:ffff::1", true)>]
[<InlineData("2001:db8::/32", "2001:db9::1", false)>]
let ``cidr matching`` (cidr: string) (ip: string) (expected: bool) =
    Assert.Equal(expected, Anonymize.ipInCidr cidr ip)

[<Fact>]
let ``mixed families never match`` () =
    Assert.False(Anonymize.ipInCidr "10.0.0.0/8" "2001:db8::1")
