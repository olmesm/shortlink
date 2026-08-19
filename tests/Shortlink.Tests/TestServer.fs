namespace Shortlink.Tests

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Shortlink.Data
open Shortlink.Web

/// Boots the full application on an in-memory test server with a
/// throw-away SQLite database. One instance per test class.
type TestApp() =
    let dataDir =
        Path.Combine(Path.GetTempPath(), "shortlink-tests", Guid.NewGuid().ToString("N"))

    do Directory.CreateDirectory dataDir |> ignore

    let dbPath = Path.Combine(dataDir, "test.db")

    let cfg =
        { AppConfig.fromLookup (fun _ -> None) with
            DefaultDomain = "example.test"
            DbDialect = Sqlite
            ConnectionString = $"Data Source={dbPath}"
            DataDir = dataDir
            AutoResolveTitles = false
            InitialAdminUsername = Some "admin"
            InitialAdminPassword = Some "test-password-123"
            RateLimitPerMinute = 10000 }

    let app =
        App.build cfg (fun builder -> builder.WebHost.UseTestServer() |> ignore)

    do app.StartAsync().GetAwaiter().GetResult()

    member _.Config = cfg
    member _.App = app
    member _.Db = app.Services.GetRequiredService<Db>()
    member _.CreateClient() : HttpClient = app.GetTestClient()

    /// Seed an API key with the given role and return the plaintext key.
    member this.CreateApiKey(role: string, ?domainId: int64, ?name: string) =
        let plain = ApiKeys.generate ()
        let insertTask =
            ApiKeyRepo.insert this.Db (ApiKeys.hash plain) (name |> Option.orElse (Some "test")) role domainId None
        insertTask.GetAwaiter().GetResult() |> ignore
        plain

    member this.AdminClient() =
        let client = this.CreateClient()
        let key = this.CreateApiKey "admin"
        client.DefaultRequestHeaders.Add("X-Api-Key", key)
        client

    interface IDisposable with
        member _.Dispose() =
            app.StopAsync().GetAwaiter().GetResult()
            (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
            try Directory.Delete(dataDir, true) with _ -> ()

[<AutoOpen>]
module Http =

    let jsonContent (json: string) =
        new StringContent(json, Encoding.UTF8, MediaTypeHeaderValue("application/json"))

    let getJson (client: HttpClient) (url: string) =
        task {
            let! response = client.GetAsync(url: string)
            let! body = response.Content.ReadAsStringAsync()
            return response, body
        }

    let postJson (client: HttpClient) (url: string) (json: string) =
        task {
            let! response = client.PostAsync(url, jsonContent json)
            let! body = response.Content.ReadAsStringAsync()
            return response, body
        }

    let patchJson (client: HttpClient) (url: string) (json: string) =
        task {
            let! response = client.PatchAsync(url, jsonContent json)
            let! body = response.Content.ReadAsStringAsync()
            return response, body
        }
