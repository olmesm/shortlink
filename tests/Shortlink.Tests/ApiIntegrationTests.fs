module Shortlink.Tests.ApiIntegrationTests

open System.Net
open System.Text.Json
open Xunit
open Shortlink.Tests

type ApiTests(app: TestApp) =

    interface IClassFixture<TestApp>

    [<Fact>]
    member _.``health endpoint needs no auth``() =
        task {
            let client = app.CreateClient()
            let! response, body = getJson client "/rest/health"
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Contains("\"pass\"", body)
        }

    [<Fact>]
    member _.``API requests without a key get problem details 401``() =
        task {
            let client = app.CreateClient()
            let! response, body = getJson client "/rest/v1/short-urls"
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType.MediaType)
            use doc = JsonDocument.Parse body
            Assert.Equal(401, doc.RootElement.GetProperty("status").GetInt32())
        }

    [<Fact>]
    member _.``short URL round trip: create, get, list, edit, delete``() =
        task {
            let client = app.AdminClient()

            // create
            let! createResponse, createBody =
                postJson client "/rest/v1/short-urls"
                    """{"longUrl":"https://example.com/round-trip","tags":["one","two"],"title":"Round trip"}"""
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode)
            use created = JsonDocument.Parse createBody
            let code = created.RootElement.GetProperty("shortCode").GetString()
            Assert.Equal("example.test", created.RootElement.GetProperty("domain").GetString())
            Assert.Equal($"http://example.test/{code}", created.RootElement.GetProperty("shortUrl").GetString())

            // get
            let! getResponse, getBody = getJson client $"/rest/v1/short-urls/{code}"
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode)
            use fetched = JsonDocument.Parse getBody
            Assert.Equal("Round trip", fetched.RootElement.GetProperty("title").GetString())
            Assert.Equal(2, fetched.RootElement.GetProperty("tags").GetArrayLength())

            // list with search
            let! _, listBody = getJson client "/rest/v1/short-urls?searchTerm=round-trip"
            use listed = JsonDocument.Parse listBody
            Assert.Equal(1, listed.RootElement.GetProperty("data").GetArrayLength())

            // edit
            let! editResponse, editBody =
                patchJson client $"/rest/v1/short-urls/{code}"
                    """{"longUrl":"https://example.com/edited","tags":["three"],"maxVisits":9}"""
            Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode)
            use edited = JsonDocument.Parse editBody
            Assert.Equal("https://example.com/edited", edited.RootElement.GetProperty("longUrl").GetString())
            Assert.Equal(9L, edited.RootElement.GetProperty("meta").GetProperty("maxVisits").GetInt64())
            Assert.Equal("three", edited.RootElement.GetProperty("tags").[0].GetString())

            // delete
            let! deleteResponse = client.DeleteAsync $"/rest/v1/short-urls/{code}"
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode)
            let! goneResponse, _ = getJson client $"/rest/v1/short-urls/{code}"
            Assert.Equal(HttpStatusCode.NotFound, goneResponse.StatusCode)
        }

    [<Fact>]
    member _.``custom slugs conflict with 409``() =
        task {
            let client = app.AdminClient()
            let! first, _ =
                postJson client "/rest/v1/short-urls"
                    """{"longUrl":"https://example.com/a","customSlug":"taken-slug"}"""
            Assert.Equal(HttpStatusCode.Created, first.StatusCode)
            let! second, body =
                postJson client "/rest/v1/short-urls"
                    """{"longUrl":"https://example.com/b","customSlug":"taken-slug"}"""
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode)
            Assert.Contains("taken-slug", body)
        }

    [<Fact>]
    member _.``findIfExists returns the existing mapping``() =
        task {
            let client = app.AdminClient()
            let! _, firstBody =
                postJson client "/rest/v1/short-urls" """{"longUrl":"https://example.com/find-me"}"""
            let! _, secondBody =
                postJson client "/rest/v1/short-urls"
                    """{"longUrl":"https://example.com/find-me","findIfExists":true}"""
            use first = JsonDocument.Parse firstBody
            use second = JsonDocument.Parse secondBody
            Assert.Equal(
                first.RootElement.GetProperty("shortCode").GetString(),
                second.RootElement.GetProperty("shortCode").GetString())
        }

    [<Fact>]
    member _.``invalid long URLs are rejected with 400``() =
        task {
            let client = app.AdminClient()
            let! response, body = postJson client "/rest/v1/short-urls" """{"longUrl":"nope"}"""
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode)
            Assert.Contains("absolute http", body)
        }

    [<Fact>]
    member _.``author keys only see their own short URLs``() =
        task {
            let authorClient = app.CreateClient()
            authorClient.DefaultRequestHeaders.Add("X-Api-Key", app.CreateApiKey "author")
            let otherClient = app.CreateClient()
            otherClient.DefaultRequestHeaders.Add("X-Api-Key", app.CreateApiKey "author")

            let! _, createBody =
                postJson authorClient "/rest/v1/short-urls" """{"longUrl":"https://example.com/mine-only"}"""
            use created = JsonDocument.Parse createBody
            let code = created.RootElement.GetProperty("shortCode").GetString()

            // The other author cannot see it.
            let! otherGet, _ = getJson otherClient $"/rest/v1/short-urls/{code}"
            Assert.Equal(HttpStatusCode.NotFound, otherGet.StatusCode)
            let! _, otherList = getJson otherClient "/rest/v1/short-urls?searchTerm=mine-only"
            use otherListed = JsonDocument.Parse otherList
            Assert.Equal(0, otherListed.RootElement.GetProperty("data").GetArrayLength())

            // The creator can.
            let! mineGet, _ = getJson authorClient $"/rest/v1/short-urls/{code}"
            Assert.Equal(HttpStatusCode.OK, mineGet.StatusCode)
        }

    [<Fact>]
    member _.``non-admin keys cannot manage api keys``() =
        task {
            let client = app.CreateClient()
            client.DefaultRequestHeaders.Add("X-Api-Key", app.CreateApiKey "author")
            let! response, _ = getJson client "/rest/v1/api-keys"
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode)
        }

    [<Fact>]
    member _.``api keys can be minted over the API and then used``() =
        task {
            let admin = app.AdminClient()
            let! createResponse, body =
                postJson admin "/rest/v1/api-keys" """{"name":"minted","role":"author"}"""
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode)
            use doc = JsonDocument.Parse body
            let key = doc.RootElement.GetProperty("apiKey").GetString()

            let minted = app.CreateClient()
            minted.DefaultRequestHeaders.Add("X-Api-Key", key)
            let! listResponse, _ = getJson minted "/rest/v1/short-urls"
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode)
        }

    [<Fact>]
    member _.``disabled api keys stop working``() =
        task {
            let admin = app.AdminClient()
            let! _, body = postJson admin "/rest/v1/api-keys" """{"name":"to-disable","role":"author"}"""
            use doc = JsonDocument.Parse body
            let key = doc.RootElement.GetProperty("apiKey").GetString()
            let id = doc.RootElement.GetProperty("id").GetInt64()

            let! patchResponse, _ = patchJson admin $"/rest/v1/api-keys/{id}" """{"enabled":false}"""
            Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode)

            let disabled = app.CreateClient()
            disabled.DefaultRequestHeaders.Add("X-Api-Key", key)
            let! response, _ = getJson disabled "/rest/v1/short-urls"
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
        }

    [<Fact>]
    member _.``tags can be listed, renamed and deleted``() =
        task {
            let client = app.AdminClient()
            let! _, _ =
                postJson client "/rest/v1/short-urls"
                    """{"longUrl":"https://example.com/tagged","tags":["rename-me","keep-me"]}"""

            let! renameResponse, _ =
                client.PutAsync("/rest/v1/tags", jsonContent """{"oldName":"rename-me","newName":"renamed"}""")
                |> fun t ->
                    task {
                        let! r = t
                        let! b = r.Content.ReadAsStringAsync()
                        return r, b
                    }
            Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode)

            let! _, listBody = getJson client "/rest/v1/tags?withStats=true&searchTerm=renamed"
            use listed = JsonDocument.Parse listBody
            Assert.Equal(1, listed.RootElement.GetProperty("data").GetArrayLength())

            let! deleteResponse = client.DeleteAsync "/rest/v1/tags?tags=renamed"
            Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode)
            let! _, afterBody = getJson client "/rest/v1/tags?searchTerm=renamed"
            use after = JsonDocument.Parse afterBody
            Assert.Equal(0, after.RootElement.GetProperty("data").GetArrayLength())
        }

    [<Fact>]
    member _.``redirect rules are validated and persisted``() =
        task {
            let client = app.AdminClient()
            let! _, createBody =
                postJson client "/rest/v1/short-urls" """{"longUrl":"https://example.com/ruled"}"""
            use created = JsonDocument.Parse createBody
            let code = created.RootElement.GetProperty("shortCode").GetString()

            let! badResponse, _ =
                postJson client $"/rest/v1/short-urls/{code}/redirect-rules"
                    """{"redirectRules":[{"longUrl":"https://example.com/x","conditions":[{"type":"nonsense","matchValue":"x"}]}]}"""
            Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode)

            let! okResponse, okBody =
                postJson client $"/rest/v1/short-urls/{code}/redirect-rules"
                    """{"redirectRules":[
                        {"longUrl":"https://example.com/android","conditions":[{"type":"device","matchValue":"android"}]},
                        {"longUrl":"https://example.com/fr","conditions":[{"type":"language","matchValue":"fr"}]}]}"""
            Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode)

            let! _, getBody = getJson client $"/rest/v1/short-urls/{code}/redirect-rules"
            use rules = JsonDocument.Parse getBody
            Assert.Equal(2, rules.RootElement.GetProperty("redirectRules").GetArrayLength())
        }

    [<Fact>]
    member _.``domains can be registered and listed``() =
        task {
            let client = app.AdminClient()
            let! createResponse, _ = postJson client "/rest/v1/domains" """{"domain":"extra.test"}"""
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode)
            let! dupResponse, _ = postJson client "/rest/v1/domains" """{"domain":"extra.test"}"""
            Assert.Equal(HttpStatusCode.Conflict, dupResponse.StatusCode)
            let! _, listBody = getJson client "/rest/v1/domains"
            Assert.Contains("extra.test", listBody)
            Assert.Contains("example.test", listBody)
        }
