namespace Shortlink.Web.Handlers

open System
open System.Threading.Tasks
open Falco
open Falco.Markup
open Microsoft.AspNetCore.Http
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

/// The public-facing side: short URL redirects, base URL, robots.txt, QR codes.
module Redirect =

    let private notFoundPage (message: string) : XmlNode =
        Elem.html
            [ Attr.lang "en" ]
            [ Elem.head
                  []
                  [ Elem.meta [ Attr.charset "utf-8" ]
                    Elem.title [] [ Text.raw "Not found" ]
                    Elem.style
                        []
                        [ Text.raw
                              "body{font-family:system-ui,sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;background:#f8fafc;color:#0f172a}
                               main{text-align:center;padding:2rem}h1{font-size:4rem;margin:0}p{color:#64748b}" ] ]
              Elem.body
                  []
                  [ Elem.main
                        []
                        [ Elem.h1 [] [ Text.raw "404" ]
                          Elem.p [] [ Text.enc message ] ] ] ]

    let private respondNotFound (message: string) : HttpHandler =
        Response.withStatusCode 404 >> Response.ofHtml (notFoundPage message)

    /// Redirect with an arbitrary 3xx status code.
    let private redirectWith (status: int) (location: string) : HttpHandler =
        fun ctx ->
            ctx.Response.StatusCode <- status
            ctx.Response.Headers.Location <- location
            Task.CompletedTask

    let private landingPage : XmlNode =
        Elem.html
            [ Attr.lang "en" ]
            [ Elem.head
                  []
                  [ Elem.meta [ Attr.charset "utf-8" ]
                    Elem.title [] [ Text.raw "Shortlink" ]
                    Elem.style
                        []
                        [ Text.raw
                              "body{font-family:system-ui,sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;background:#f8fafc;color:#0f172a}
                               main{text-align:center;padding:2rem}a{color:#2563eb}" ] ]
              Elem.body
                  []
                  [ Elem.main
                        []
                        [ Elem.h1 [] [ Text.raw "Shortlink" ]
                          Elem.p [] [ Text.raw "A self-hosted URL shortener." ]
                          Elem.p [] [ Elem.a [ Attr.href "/admin" ] [ Text.raw "Open the dashboard" ] ] ] ] ]

    /// GET / — orphan-tracked; redirects when a base-url redirect is configured.
    let baseUrl: HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let cfg = svc<AppConfig> ctx
                let! domain = Services.resolveRequestDomain db ctx.Request.Host.Value
                do! Tracking.record ctx OrphanBaseUrl None
                let target = domain.BaseUrlRedirect |> Option.orElse cfg.BaseUrlRedirect
                match target with
                | Some url -> return! Response.redirectTemporarily url ctx
                | None -> return! Response.ofHtml landingPage ctx
            }
            :> Task

    /// GET /robots.txt — disallow everything except crawlable short URLs and the base URL.
    let robots: HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let! crawlable = ShortUrlRepo.listCrawlable db
                let lines =
                    [ yield "User-agent: *"
                      for code in crawlable do
                          yield $"Allow: /{code}"
                      yield "Allow: /$"
                      yield "Disallow: /" ]
                return! Response.ofPlainText (String.Join("\n", lines) + "\n") ctx
            }
            :> Task

    /// GET /{code}/qr-code — public QR code for an existing short URL.
    let qrCode: HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let cfg = svc<AppConfig> ctx
                let route = Request.getRoute ctx
                let code = route.GetString "code"
                let! domain = Services.resolveRequestDomain db ctx.Request.Host.Value
                let! shortUrl = ShortUrlRepo.tryGetByCode db domain.Id code
                match shortUrl with
                | None -> return! respondNotFound "There is no short URL to encode." ctx
                | Some su ->
                    let q = Request.getQuery ctx
                    let tryInt (name: string) =
                        match q.TryGetString name with
                        | Some v ->
                            match Int32.TryParse v with
                            | true, i -> Some i
                            | _ -> None
                        | None -> None
                    let opts =
                        Qr.parseOptions (tryInt "size") (tryInt "margin") (q.TryGetString "errorCorrection") (q.TryGetString "format")
                    let content = Services.shortUrlFor cfg domain.Authority su.ShortCode
                    return! Qr.respond content opts ctx
            }
            :> Task

    let private visitorContext (ctx: HttpContext) : VisitorContext =
        let query =
            ctx.Request.Query
            |> Seq.map (fun kv -> kv.Key, (if kv.Value.Count > 0 then string kv.Value.[0] else ""))
            |> Map.ofSeq
        { UserAgent =
            match ctx.Request.Headers.UserAgent |> string with
            | "" -> None
            | ua -> Some ua
          AcceptLanguage =
            match ctx.Request.Headers.AcceptLanguage |> string with
            | "" -> None
            | al -> Some al
          Query = query
          RemoteIp = Tracking.remoteIp ctx }

    /// Handle a missing/inactive short URL: orphan tracking + configured fallbacks.
    let private handleInvalid (slug: string) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let cfg = svc<AppConfig> ctx
                let! domain = Services.resolveRequestDomain db ctx.Request.Host.Value
                let looksLikeShortCode = ShortCode.validateSlug slug |> Result.isOk
                let visitType =
                    if looksLikeShortCode then OrphanInvalidShortUrl else OrphanRegular404
                do! Tracking.record ctx visitType None
                let target =
                    if looksLikeShortCode then
                        domain.InvalidShortUrlRedirect |> Option.orElse cfg.InvalidShortUrlRedirect
                    else
                        domain.Regular404Redirect |> Option.orElse cfg.Regular404Redirect
                match target with
                | Some url -> return! Response.redirectTemporarily url ctx
                | None -> return! respondNotFound "This short URL does not exist." ctx
            }
            :> Task

    /// GET /{**slug} — the redirect hot path.
    let shortUrl: HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let cfg = svc<AppConfig> ctx
                let route = Request.getRoute ctx
                let slug = (route.GetString "slug").Trim('/')

                if slug = "" then
                    return! baseUrl ctx
                else
                    let! domain = Services.resolveRequestDomain db ctx.Request.Host.Value
                    let! found = ShortUrlRepo.tryGetByCode db domain.Id slug
                    match found with
                    | None -> return! handleInvalid slug ctx
                    | Some su ->
                        let! visitCount =
                            match su.MaxVisits with
                            | Some _ -> ShortUrlRepo.countValidVisits db su.Id
                            | None -> Task.FromResult 0L
                        match Services.checkActive su visitCount DateTime.UtcNow with
                        | Error _ -> return! handleInvalid slug ctx
                        | Ok() ->
                            let visitor = visitorContext ctx
                            let! rules = ShortUrlRepo.getRules db su.Id
                            let target = RedirectRules.resolveTarget su.LongUrl rules visitor

                            let finalUrl =
                                if su.ForwardQuery then
                                    let skipParam = cfg.TrackSkipParam
                                    let incoming =
                                        ctx.Request.Query
                                        |> Seq.filter (fun kv ->
                                            match skipParam with
                                            | Some p -> not (String.Equals(kv.Key, p, StringComparison.OrdinalIgnoreCase))
                                            | None -> true)
                                        |> Seq.collect (fun kv ->
                                            if kv.Value.Count = 0 then [ kv.Key, "" ]
                                            else [ for v in kv.Value -> kv.Key, string v ])
                                        |> List.ofSeq
                                    Validation.forwardQuery target incoming
                                else
                                    target

                            do!
                                Tracking.record ctx ValidShortUrl (
                                    Some(su.Id,
                                         { Tracking.VisitedShortUrl.ShortCode = su.ShortCode
                                           Tracking.VisitedShortUrl.Domain = domain.Authority
                                           Tracking.VisitedShortUrl.LongUrl = su.LongUrl }))

                            return! redirectWith su.RedirectStatus finalUrl ctx
            }
            :> Task
