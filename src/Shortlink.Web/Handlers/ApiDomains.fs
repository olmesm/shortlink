namespace Shortlink.Web.Handlers

open System.Threading.Tasks
open Falco
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

type CreateDomainBody = { domain: string }

type DomainRedirectsBody =
    { domain: string
      baseUrlRedirect: string option
      regular404Redirect: string option
      invalidShortUrlRedirect: string option }

module ApiDomains =

    let private domainDto (d: DomainRow) =
        {| domain = d.Authority
           isDefault = d.IsDefault
           redirects =
            {| baseUrlRedirect = d.BaseUrlRedirect
               regular404Redirect = d.Regular404Redirect
               invalidShortUrlRedirect = d.InvalidShortUrlRedirect |} |}

    /// GET /rest/v1/domains
    let list (_key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let! domains = DomainRepo.list db
                return! Json.respond {| data = domains |> List.map domainDto |} ctx
            }
            :> Task

    /// POST /rest/v1/domains (admin)
    let create (_key: ApiKeyRow) : HttpHandler =
        Api.withJson<CreateDomainBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    match Validation.validateDomainAuthority body.domain with
                    | Error e -> return! Problems.badRequest e ctx
                    | Ok authority ->
                        let! created = DomainRepo.create db authority
                        match created with
                        | Some d -> return! (Response.withStatusCode 201 >> Json.respond (domainDto d)) ctx
                        | None ->
                            return! Problems.conflict "domain-exists" $"Domain '{authority}' is already registered." ctx
                }
                :> Task)

    /// PATCH /rest/v1/domains/redirects (admin)
    let setRedirects (_key: ApiKeyRow) : HttpHandler =
        Api.withJson<DomainRedirectsBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! domain = DomainRepo.tryGetByAuthority db (body.domain.Trim().ToLowerInvariant())
                    match domain with
                    | None -> return! Problems.notFound $"Domain '{body.domain}' is not registered." ctx
                    | Some d ->
                        let! _ =
                            DomainRepo.updateRedirects db d.Id body.baseUrlRedirect body.regular404Redirect
                                body.invalidShortUrlRedirect
                        let! updated = DomainRepo.tryGetById db d.Id
                        return! Json.respond (domainDto updated.Value) ctx
                }
                :> Task)

    /// DELETE /rest/v1/domains/{authority} (admin)
    let delete (_key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let authority = (Request.getRoute ctx).GetString "authority"
                let! domain = DomainRepo.tryGetByAuthority db (authority.ToLowerInvariant())
                match domain with
                | None -> return! Problems.notFound $"Domain '{authority}' is not registered." ctx
                | Some d when d.IsDefault ->
                    return! Problems.forbidden "The default domain cannot be deleted." ctx
                | Some d ->
                    let! _ = DomainRepo.delete db d.Id
                    return! (Response.withStatusCode 204 >> Response.ofEmpty) ctx
            }
            :> Task

    /// GET /rest/v1/domains/{authority}/visits
    let visits (key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let authority = (Request.getRoute ctx).GetString "authority"
                let! domain = DomainRepo.tryGetByAuthority db (authority.ToLowerInvariant())
                match domain with
                | None -> return! Problems.notFound $"Domain '{authority}' is not registered." ctx
                | Some d ->
                    let allowed =
                        match ApiKeys.roleOf key with
                        | AdminKey -> true
                        | DomainKey domainId -> domainId = d.Id
                        | AuthorKey -> false
                    if not allowed then
                        return! Problems.forbidden "This API key cannot view visits for this domain." ctx
                    else
                        let q = Request.getQuery ctx
                        let! page = VisitRepo.listForDomain db d.Id (Api.visitFiltersFromQuery q)
                        return! Json.respond (Api.pageDto Api.visitDto page) ctx
            }
            :> Task
