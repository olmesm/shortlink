namespace Shortlink.Web.Handlers

open System.Threading.Tasks
open Falco
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

module ApiVisits =

    /// Build a stats scope from query params (?shortCode=&domain=&tag=&orphan=true).
    let private scopeFromQuery (db: Db) (key: AuthenticatedKey) (q: RequestData) : Task<Result<VisitScope, HttpHandler>> =
        task {
            if Api.queryBool q "orphan" then
                match key.Role with
                | ApiKeyRole.Admin -> return Ok VisitScope.Orphan
                | _ -> return Error(Problems.forbidden "Only admin keys can query orphan visit stats.")
            else
                match q.TryGetString "shortCode", q.TryGetString "tag", q.TryGetString "domain" with
                | Some code, _, _ ->
                    let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")
                    return found |> Result.map (fun d -> VisitScope.ShortUrl(ShortUrlId d.Id))
                | None, Some tag, _ -> return Ok(VisitScope.Tag tag)
                | None, None, Some authority ->
                    let! d = DomainRepo.tryGetByAuthority db (authority.ToLowerInvariant())

                    match d with
                    | None -> return Error(Problems.notFound $"Domain '{authority}' is not registered.")
                    | Some d -> return Ok(VisitScope.Domain(DomainId d.Id))
                | None, None, None ->
                    match key.Role with
                    | ApiKeyRole.Admin -> return Ok VisitScope.Global
                    | ApiKeyRole.Domain domainId -> return Ok(VisitScope.Domain domainId)
                    | ApiKeyRole.Author ->
                        return Error(Problems.forbidden "Author keys must scope stats to a shortCode.")
        }

    /// GET /rest/v1/visits — global counters.
    let overview (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx

                match key.Role with
                | ApiKeyRole.Admin ->
                    let! o = StatsRepo.overview db

                    return!
                        Json.respond
                            {| VisitsCount = o.VisitCount
                               OrphanVisitsCount = o.OrphanVisitCount
                               ShortUrlsCount = o.ShortUrlCount
                               TagsCount = o.TagCount
                               BotVisitsCount = o.BotVisitCount |}
                            ctx
                | _ -> return! Problems.forbidden "Only admin keys can view the global visit summary." ctx
            }
            :> Task

    /// GET /rest/v1/visits/non-orphan
    let listNonOrphan (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx

                match key.Role with
                | ApiKeyRole.Admin ->
                    let q = Request.getQuery ctx
                    let! page = VisitRepo.listNonOrphan db (Api.visitFiltersFromQuery q)
                    return! Json.respond (Api.pageDto Api.visitDto page) ctx
                | _ -> return! Problems.forbidden "Only admin keys can list all visits." ctx
            }
            :> Task

    /// GET /rest/v1/visits/orphan?type=
    let listOrphan (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx

                match key.Role with
                | ApiKeyRole.Admin ->
                    let q = Request.getQuery ctx
                    let visitType = q.TryGetString "type" |> Option.bind VisitType.OfSlug
                    let! page = VisitRepo.listOrphan db visitType (Api.visitFiltersFromQuery q)
                    return! Json.respond (Api.pageDto Api.visitDto page) ctx
                | _ -> return! Problems.forbidden "Only admin keys can list orphan visits." ctx
            }
            :> Task

    /// DELETE /rest/v1/visits/orphan
    let deleteOrphan (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx

                match key.Role with
                | ApiKeyRole.Admin ->
                    let! deleted = VisitRepo.deleteOrphan db
                    return! Json.respond {| DeletedVisits = deleted |} ctx
                | _ -> return! Problems.forbidden "Only admin keys can delete orphan visits." ctx
            }
            :> Task

    /// GET /rest/v1/stats/visits-per-day
    let visitsPerDay (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let q = Request.getQuery ctx
                let! scope = scopeFromQuery db key q

                match scope with
                | Error handler -> return! handler ctx
                | Ok scope ->
                    let! series = StatsRepo.visitsPerDay db scope (Api.queryDate q "startDate") (Api.queryDate q "endDate")

                    return!
                        Json.respond
                            {| Data = series |> List.map (fun (day, count) -> {| Date = day; Count = count |}) |}
                            ctx
            }
            :> Task

    /// GET /rest/v1/stats/breakdown?by=country|city|browser|os|referer|device
    let breakdown (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let q = Request.getQuery ctx

                let column =
                    match q.TryGetString "by" |> Option.map (fun b -> b.ToLowerInvariant()) with
                    | Some "country" -> Some "country_name"
                    | Some "countrycode" -> Some "country_code"
                    | Some "city" -> Some "city"
                    | Some "browser" -> Some "browser"
                    | Some "os" -> Some "os"
                    | Some "referer"
                    | Some "referrer" -> Some "referer"
                    | Some "device" -> Some "device"
                    | _ -> None

                match column with
                | None ->
                    return!
                        Problems.badRequest "Provide ?by= one of: country, countryCode, city, browser, os, referer, device."
                            ctx
                | Some column ->
                    let! scope = scopeFromQuery db key q

                    match scope with
                    | Error handler -> return! handler ctx
                    | Ok scope ->
                        let limit = Api.queryInt q "limit" |> Option.defaultValue 25

                        let! rows =
                            StatsRepo.breakdown db scope column (Api.queryDate q "startDate")
                                (Api.queryDate q "endDate") (min 100 (max 1 limit))

                        return!
                            Json.respond
                                {| Data =
                                    rows
                                    |> List.map (fun (label, count) ->
                                        {| Value = label |> Option.defaultValue "Unknown"
                                           Count = count |}) |}
                                ctx
            }
            :> Task
