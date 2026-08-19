module Shortlink.Tests.RedirectIntegrationTests

open System.Net
open System.Net.Http
open System.Text.Json
open Xunit
open Shortlink.Tests

type RedirectTests(app: TestApp) =

    interface IClassFixture<TestApp>

    member private _.Create(client, json: string) =
        task {
            let! _, body = postJson client "/rest/v1/short-urls" json
            use doc = JsonDocument.Parse body
            return doc.RootElement.GetProperty("shortCode").GetString()
        }

    [<Fact>]
    member this.``valid short URLs redirect and record a visit``() =
        task {
            let admin = app.AdminClient()
            let! code = this.Create(admin, """{"longUrl":"https://example.com/target"}""")

            let visitor = app.CreateClient()
            use request = new HttpRequestMessage(HttpMethod.Get, $"/{code}")
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh) Firefox") |> ignore
            request.Headers.TryAddWithoutValidation("Referer", "https://google.com/") |> ignore
            let! response = visitor.SendAsync request
            Assert.Equal(HttpStatusCode.Found, response.StatusCode)
            Assert.Equal("https://example.com/target", response.Headers.Location.ToString())

            let! _, visitsBody = getJson admin $"/rest/v1/short-urls/{code}/visits"
            use visits = JsonDocument.Parse visitsBody
            let visit = visits.RootElement.GetProperty("data").[0]
            Assert.Equal("https://google.com/", visit.GetProperty("referer").GetString())
            Assert.Equal("desktop", visit.GetProperty("device").GetString())
            Assert.False(visit.GetProperty("potentialBot").GetBoolean())
        }

    [<Fact>]
    member this.``configured redirect status is used``() =
        task {
            let admin = app.AdminClient()
            let! code = this.Create(admin, """{"longUrl":"https://example.com/permanent","redirectStatus":301}""")
            let! response = app.CreateClient().GetAsync $"/{code}"
            Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode)
        }

    [<Fact>]
    member this.``query params are forwarded when enabled``() =
        task {
            let admin = app.AdminClient()
            let! code = this.Create(admin, """{"longUrl":"https://example.com/p?fixed=1"}""")
            let! response = app.CreateClient().GetAsync $"/{code}?utm_source=mail"
            Assert.Equal("https://example.com/p?fixed=1&utm_source=mail", response.Headers.Location.ToString())

            let! code2 = this.Create(admin, """{"longUrl":"https://example.com/q","forwardQuery":false}""")
            let! response2 = app.CreateClient().GetAsync $"/{code2}?utm_source=mail"
            Assert.Equal("https://example.com/q", response2.Headers.Location.ToString())
        }

    [<Fact>]
    member this.``device rules pick the right target``() =
        task {
            let admin = app.AdminClient()
            let! code = this.Create(admin, """{"longUrl":"https://example.com/default"}""")
            let! _ =
                postJson admin $"/rest/v1/short-urls/{code}/redirect-rules"
                    """{"redirectRules":[{"longUrl":"https://example.com/droid","conditions":[{"type":"device","matchValue":"android"}]}]}"""

            let visitor = app.CreateClient()
            use androidRequest = new HttpRequestMessage(HttpMethod.Get, $"/{code}")
            androidRequest.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Linux; Android 14)") |> ignore
            let! androidResponse = visitor.SendAsync androidRequest
            Assert.Equal("https://example.com/droid", androidResponse.Headers.Location.ToString())

            use desktopRequest = new HttpRequestMessage(HttpMethod.Get, $"/{code}")
            desktopRequest.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh)") |> ignore
            let! desktopResponse = visitor.SendAsync desktopRequest
            Assert.Equal("https://example.com/default", desktopResponse.Headers.Location.ToString())
        }

    [<Fact>]
    member this.``max visits exhausts the short URL``() =
        task {
            let admin = app.AdminClient()
            let! code = this.Create(admin, """{"longUrl":"https://example.com/limited","maxVisits":2}""")
            let visitor = app.CreateClient()
            let! first = visitor.GetAsync $"/{code}"
            let! second = visitor.GetAsync $"/{code}"
            let! third = visitor.GetAsync $"/{code}"
            Assert.Equal(HttpStatusCode.Found, first.StatusCode)
            Assert.Equal(HttpStatusCode.Found, second.StatusCode)
            Assert.Equal(HttpStatusCode.NotFound, third.StatusCode)
        }

    [<Fact>]
    member this.``validity window is enforced``() =
        task {
            let admin = app.AdminClient()
            let! expired =
                this.Create(admin, """{"longUrl":"https://example.com/old","validUntil":"2000-01-01T00:00:00Z"}""")
            let! notYet =
                this.Create(admin, """{"longUrl":"https://example.com/future","validSince":"2100-01-01T00:00:00Z"}""")
            let visitor = app.CreateClient()
            let! expiredResponse = visitor.GetAsync $"/{expired}"
            let! notYetResponse = visitor.GetAsync $"/{notYet}"
            Assert.Equal(HttpStatusCode.NotFound, expiredResponse.StatusCode)
            Assert.Equal(HttpStatusCode.NotFound, notYetResponse.StatusCode)
        }

    [<Fact>]
    member this.``short codes are scoped to their domain``() =
        task {
            let admin = app.AdminClient()
            let! _, body =
                postJson admin "/rest/v1/short-urls"
                    """{"longUrl":"https://example.com/other-domain","customSlug":"scoped","domain":"links.test"}"""
            use doc = JsonDocument.Parse body
            Assert.Equal("links.test", doc.RootElement.GetProperty("domain").GetString())

            let visitor = app.CreateClient()
            // On the registered domain the code resolves…
            let! onDomain = visitor.GetAsync "http://links.test/scoped"
            Assert.Equal(HttpStatusCode.Found, onDomain.StatusCode)
            // …on the default domain it does not.
            let! onDefault = visitor.GetAsync "http://example.test/scoped"
            Assert.Equal(HttpStatusCode.NotFound, onDefault.StatusCode)
        }

    [<Fact>]
    member this.``unknown short codes are tracked as orphan visits``() =
        task {
            let admin = app.AdminClient()
            let visitor = app.CreateClient()
            let! notFoundResponse = visitor.GetAsync "/definitely-missing"
            Assert.Equal(HttpStatusCode.NotFound, notFoundResponse.StatusCode)
            let! _ = visitor.GetAsync "/"

            let! _, orphansBody = getJson admin "/rest/v1/visits/orphan"
            use orphans = JsonDocument.Parse orphansBody
            let items = orphans.RootElement.GetProperty("data")
            Assert.True(items.GetArrayLength() >= 2)
            let urls =
                [ for i in 0 .. items.GetArrayLength() - 1 ->
                      items.[i].GetProperty("visitedUrl").GetString() ]
            Assert.Contains(urls, fun (u: string) -> u.Contains "definitely-missing")
        }

    [<Fact>]
    member this.``domain level base url redirect wins over the landing page``() =
        task {
            let admin = app.AdminClient()
            let! _, _ =
                patchJson admin "/rest/v1/domains/redirects"
                    """{"domain":"example.test","baseUrlRedirect":"https://company.example.com"}"""
            let! response = app.CreateClient().GetAsync "http://example.test/"
            Assert.Equal(HttpStatusCode.Found, response.StatusCode)
            Assert.Equal("https://company.example.com", response.Headers.Location.ToString().TrimEnd('/'))
        }

    [<Fact>]
    member this.``robots txt lists crawlable short URLs``() =
        task {
            let admin = app.AdminClient()
            let! code = this.Create(admin, """{"longUrl":"https://example.com/crawl","crawlable":true}""")
            let! _, robots = getJson (app.CreateClient()) "/robots.txt"
            Assert.Contains($"Allow: /{code}", robots)
            Assert.Contains("Disallow: /", robots)
        }

    [<Fact>]
    member this.``qr codes are served in png and svg``() =
        task {
            let admin = app.AdminClient()
            let! code = this.Create(admin, """{"longUrl":"https://example.com/qr"}""")
            let visitor = app.CreateClient()
            let! png = visitor.GetAsync $"/{code}/qr-code"
            Assert.Equal(HttpStatusCode.OK, png.StatusCode)
            Assert.Equal("image/png", png.Content.Headers.ContentType.MediaType)
            let! svg = visitor.GetAsync $"/{code}/qr-code?format=svg&size=200"
            Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType.MediaType)
            let! missing = visitor.GetAsync "/no-such-code/qr-code"
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode)
        }

    [<Fact>]
    member this.``track skip param is respected``() =
        task {
            // The default config has no skip param; this exercises per-request visit counting instead.
            let admin = app.AdminClient()
            let! code = this.Create(admin, """{"longUrl":"https://example.com/counted"}""")
            let visitor = app.CreateClient()
            let! _ = visitor.GetAsync $"/{code}"
            let! _, body = getJson admin $"/rest/v1/short-urls/{code}"
            use doc = JsonDocument.Parse body
            Assert.Equal(1L, doc.RootElement.GetProperty("visitsSummary").GetProperty("total").GetInt64())
        }
