namespace Shortlink.Web

open System
open System.IO
open System.Threading.RateLimiting
open System.Threading.Tasks
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.HttpOverrides
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Shortlink.Data
open Shortlink.Web.Handlers

module App =

    let addServices (cfg: AppConfig) (services: IServiceCollection) =
        Directory.CreateDirectory cfg.DataDir |> ignore
        let db = Db.create cfg.DbDialect cfg.ConnectionString

        services
            .AddSingleton<AppConfig>(cfg)
            .AddSingleton<Db>(db)
            .AddSingleton<WorkQueues>()
            .AddSingleton<GeoIpService>()
        |> ignore

        services.AddHttpClient("titles", fun c -> c.Timeout <- TimeSpan.FromSeconds 10.0) |> ignore
        services.AddHttpClient("webhooks", fun c -> c.Timeout <- TimeSpan.FromSeconds 15.0) |> ignore
        services.AddHttpClient("geoip", fun c -> c.Timeout <- TimeSpan.FromMinutes 5.0) |> ignore

        services
            .AddHostedService<GeoWorker>()
            .AddHostedService<TitleWorker>()
            .AddHostedService<WebhookWorker>()
            .AddHostedService<GeoDbUpdater>()
        |> ignore

        services
            .AddDataProtection()
            .PersistKeysToFileSystem(DirectoryInfo(Path.Combine(cfg.DataDir, "keys")))
        |> ignore

        services
            .AddAuthentication(UiAuth.scheme)
            .AddCookie(fun options ->
                options.Cookie.Name <- "shortlink_session"
                options.Cookie.HttpOnly <- true
                options.Cookie.SameSite <- SameSiteMode.Lax
                options.LoginPath <- PathString "/admin/login"
                options.ExpireTimeSpan <- TimeSpan.FromDays 14.0)
        |> ignore

        services.AddAuthorization() |> ignore
        services

    /// Run migrations, register the default domain and bootstrap the first admin user.
    let initialize (cfg: AppConfig) (db: Db) (logger: ILogger) : Task<unit> =
        task {
            Migrations.run db
            let! _ = DomainRepo.ensureDefault db (cfg.DefaultDomain.ToLowerInvariant())

            let! userCount = UserRepo.count db
            if userCount = 0L then
                let username = cfg.InitialAdminUsername |> Option.defaultValue "admin"
                let password, generated =
                    match cfg.InitialAdminPassword with
                    | Some p -> p, false
                    | None ->
                        let bytes = Security.Cryptography.RandomNumberGenerator.GetBytes(12)
                        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'), true
                let! created = UserRepo.insert db username (Passwords.hash password) "admin"
                match created with
                | Some _ when generated ->
                    logger.LogWarning(
                        "Created initial admin user '{Username}' with generated password: {Password} — "
                        + "log in at /admin/login and change it.",
                        username, password)
                | Some _ ->
                    logger.LogInformation("Created initial admin user '{Username}'.", username)
                | None -> ()
        }

    /// Rate limiting for mutating REST calls, partitioned by client IP.
    let private rateLimitMiddleware (cfg: AppConfig) : Func<HttpContext, RequestDelegate, Task> =
        let limiter =
            PartitionedRateLimiter.Create<string, string>(fun (key: string) ->
                RateLimitPartition.GetFixedWindowLimiter(
                    key,
                    fun _ ->
                        FixedWindowRateLimiterOptions(
                            PermitLimit = cfg.RateLimitPerMinute,
                            Window = TimeSpan.FromMinutes 1.0,
                            QueueLimit = 0)))
        Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
            let isMutatingRest =
                ctx.Request.Path.StartsWithSegments(PathString "/rest")
                && ctx.Request.Method <> "GET"
                && ctx.Request.Method <> "HEAD"
            if isMutatingRest && cfg.RateLimitPerMinute > 0 then
                let key =
                    match ctx.Connection.RemoteIpAddress with
                    | null -> "unknown"
                    | ip -> ip.ToString()
                use lease = limiter.AttemptAcquire(key)
                if lease.IsAcquired then
                    next.Invoke ctx
                else
                    Problems.problem 429 "rate-limit" "Too many requests"
                        "Rate limit exceeded; retry in a minute." ctx
            else
                next.Invoke ctx)

    let apiEndpoints =
        [ get "/rest/health" Health.check

          // Short URLs
          get "/rest/v1/short-urls" (ApiAuth.requireApiKey ApiShortUrls.list)
          post "/rest/v1/short-urls" (ApiAuth.requireApiKey ApiShortUrls.create)
          get "/rest/v1/short-urls/{code}" (ApiAuth.requireApiKey ApiShortUrls.get)
          patch "/rest/v1/short-urls/{code}" (ApiAuth.requireApiKey ApiShortUrls.edit)
          delete "/rest/v1/short-urls/{code}" (ApiAuth.requireApiKey ApiShortUrls.delete)
          get "/rest/v1/short-urls/{code}/redirect-rules" (ApiAuth.requireApiKey ApiShortUrls.getRules)
          post "/rest/v1/short-urls/{code}/redirect-rules" (ApiAuth.requireApiKey ApiShortUrls.setRules)
          get "/rest/v1/short-urls/{code}/visits" (ApiAuth.requireApiKey ApiShortUrls.listVisits)
          delete "/rest/v1/short-urls/{code}/visits" (ApiAuth.requireApiKey ApiShortUrls.deleteVisits)

          // Tags
          get "/rest/v1/tags" (ApiAuth.requireApiKey ApiTags.list)
          put "/rest/v1/tags" (ApiAuth.requireApiKey ApiTags.rename)
          delete "/rest/v1/tags" (ApiAuth.requireApiKey ApiTags.delete)
          get "/rest/v1/tags/{tag}/visits" (ApiAuth.requireApiKey ApiTags.visits)

          // Domains
          get "/rest/v1/domains" (ApiAuth.requireApiKey ApiDomains.list)
          post "/rest/v1/domains" (ApiAuth.requireAdminKey ApiDomains.create)
          patch "/rest/v1/domains/redirects" (ApiAuth.requireAdminKey ApiDomains.setRedirects)
          delete "/rest/v1/domains/{authority}" (ApiAuth.requireAdminKey ApiDomains.delete)
          get "/rest/v1/domains/{authority}/visits" (ApiAuth.requireApiKey ApiDomains.visits)

          // Visits & stats
          get "/rest/v1/visits" (ApiAuth.requireApiKey ApiVisits.overview)
          get "/rest/v1/visits/non-orphan" (ApiAuth.requireApiKey ApiVisits.listNonOrphan)
          get "/rest/v1/visits/orphan" (ApiAuth.requireApiKey ApiVisits.listOrphan)
          delete "/rest/v1/visits/orphan" (ApiAuth.requireApiKey ApiVisits.deleteOrphan)
          get "/rest/v1/stats/visits-per-day" (ApiAuth.requireApiKey ApiVisits.visitsPerDay)
          get "/rest/v1/stats/breakdown" (ApiAuth.requireApiKey ApiVisits.breakdown)

          // API keys
          get "/rest/v1/api-keys" (ApiAuth.requireAdminKey ApiApiKeys.list)
          post "/rest/v1/api-keys" (ApiAuth.requireAdminKey ApiApiKeys.create)
          patch "/rest/v1/api-keys/{id}" (ApiAuth.requireAdminKey ApiApiKeys.patch)
          delete "/rest/v1/api-keys/{id}" (ApiAuth.requireAdminKey ApiApiKeys.delete)

          // Webhooks
          get "/rest/v1/webhooks" (ApiAuth.requireAdminKey ApiWebhooks.list)
          post "/rest/v1/webhooks" (ApiAuth.requireAdminKey ApiWebhooks.create)
          patch "/rest/v1/webhooks/{id}" (ApiAuth.requireAdminKey ApiWebhooks.patch)
          delete "/rest/v1/webhooks/{id}" (ApiAuth.requireAdminKey ApiWebhooks.delete) ]

    let publicEndpoints =
        [ get "/robots.txt" Redirect.robots
          get "/{code}/qr-code" Redirect.qrCode
          head "/{code}/qr-code" Redirect.qrCode
          get "/" Redirect.baseUrl
          get "/{**slug}" Redirect.shortUrl
          head "/{**slug}" Redirect.shortUrl ]

    let endpoints () = apiEndpoints @ Ui.Routes.endpoints @ publicEndpoints

    /// Build the application. `customize` lets tests plug in a TestServer.
    let build (cfg: AppConfig) (customize: WebApplicationBuilder -> unit) : WebApplication =
        let builder = WebApplication.CreateBuilder()
        customize builder
        addServices cfg builder.Services |> ignore

        let wapp = builder.Build()

        let db = wapp.Services.GetRequiredService<Db>()
        let logger = wapp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shortlink.Init")
        (initialize cfg db logger).GetAwaiter().GetResult()

        wapp.UseForwardedHeaders(
            ForwardedHeadersOptions(ForwardedHeaders = (ForwardedHeaders.XForwardedFor ||| ForwardedHeaders.XForwardedProto)))
        |> ignore

        wapp.UseStaticFiles() |> ignore
        wapp.UseAuthentication() |> ignore
        wapp.Use(rateLimitMiddleware cfg) |> ignore

        wapp.UseRouting().UseFalco(endpoints ()) |> ignore
        wapp
