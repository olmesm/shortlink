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
                  [ Elem.main [] [ Elem.h1 [] [ Text.raw "404" ]; Elem.p [] [ Text.enc message ] ] ] ]

    let private respondNotFound (message: string) : HttpHandler =
        Response.withStatusCode 404 >> Response.ofHtml (notFoundPage message)

    /// Redirect with an arbitrary 3xx status code.
    let private redirectWith (status: RedirectStatus) (location: string) : HttpHandler =
        fun ctx ->
            ctx.Response.StatusCode <- status.Code
            ctx.Response.Headers.Location <- location
            Task.CompletedTask

    let private landingPage: XmlNode =
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
                do! Tracking.record ctx VisitType.OrphanBaseUrl None
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
                let code = (Request.getRoute ctx).GetString "code"
                let! domain = Services.resolveRequestDomain db ctx.Request.Host.Value
                let! shortUrl = ShortUrlRepo.tryGetByCode db (DomainId domain.Id) code

                match shortUrl with
                | None -> return! respondNotFound "There is no short URL to encode." ctx
                | Some su ->
                    let q = Request.getQuery ctx

                    let tryInt (name: string) =
                        q.TryGetString name
                        |> Option.bind (fun v ->
                            match Int32.TryParse v with
                            | true, i -> Some i
                            | _ -> None)

                    let opts =
                        Qr.parseOptions (tryInt "size") (tryInt "margin") (q.TryGetString "errorCorrection")
                            (q.TryGetString "format")

                    let content = Dto.shortUrlFor cfg domain.Authority su.ShortCode
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

    /// Does a missed path look like a short code the visitor mistyped (as
    /// opposed to a scanner probing /wp-admin/setup.php and the like)?
    /// Single path segment, code-like length, no dots.
    let private looksLikeShortCode (slug: string) =
        not (slug.Contains '/')
        && not (slug.Contains '.')
        && slug.Length <= 64
        && ShortCode.isValidSlug slug

    /// Handle a missing/inactive short URL: orphan tracking + configured fallbacks.
    let private handleInvalid (slug: string) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let cfg = svc<AppConfig> ctx
                let! domain = Services.resolveRequestDomain db ctx.Request.Host.Value
                let isCodeLike = looksLikeShortCode slug

                let visitType =
                    if isCodeLike then VisitType.OrphanInvalidShortUrl else VisitType.OrphanRegular404

                do! Tracking.record ctx visitType None

                let target =
                    if isCodeLike then
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
                let slug = ((Request.getRoute ctx).GetString "slug").Trim('/')

                if slug = "" then
                    return! baseUrl ctx
                else
                    let! domain = Services.resolveRequestDomain db ctx.Request.Host.Value
                    let! found = ShortUrlRepo.tryGetByCode db (DomainId domain.Id) slug

                    match found with
                    | None -> return! handleInvalid slug ctx
                    | Some su ->
                        let id = ShortUrlId su.Id
                        let lifetime = Services.lifetimeOfRow su

                        let! visitCount =
                            match lifetime.MaxVisits with
                            | Some _ -> ShortUrlRepo.countValidVisits db id
                            | None -> Task.FromResult 0L

                        match Lifetime.checkActive DateTime.UtcNow visitCount lifetime with
                        | Error _ -> return! handleInvalid slug ctx
                        | Ok() ->
                            let visitor = visitorContext ctx
                            let! rules = ShortUrlRepo.getRules db id
                            let target = RedirectRules.resolveTarget su.LongUrl rules visitor

                            let finalUrl =
                                if su.ForwardQuery then
                                    let incoming =
                                        ctx.Request.Query
                                        |> Seq.filter (fun kv ->
                                            match cfg.TrackSkipParam with
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
                                let visited: VisitedShortUrl =
                                    { ShortCode = su.ShortCode
                                      Domain = domain.Authority
                                      LongUrl = su.LongUrl }

                                Tracking.record ctx VisitType.ValidShortUrl (Some(id, visited))

                            let status =
                                RedirectStatus.OfCode su.RedirectStatus
                                |> Option.defaultValue cfg.DefaultRedirectStatus

                            return! redirectWith status finalUrl ctx
            }
            :> Task
