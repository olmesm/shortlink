namespace Shortlink.Web.Handlers

open System.Threading.Tasks
open Falco
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

type CreateDomainBody = { Domain: string }

type DomainRedirectsBody =
    { Domain: string
      BaseUrlRedirect: string option
      Regular404Redirect: string option
      InvalidShortUrlRedirect: string option }

module ApiDomains =

    let private domainDto (d: DomainRow) =
        {| Domain = d.Authority
           IsDefault = d.IsDefault
           Redirects =
            {| BaseUrlRedirect = d.BaseUrlRedirect
               Regular404Redirect = d.Regular404Redirect
               InvalidShortUrlRedirect = d.InvalidShortUrlRedirect |} |}

    /// GET /rest/v1/domains
    let list (_key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let! domains = DomainRepo.list db
                return! Json.respond {| Data = domains |> List.map domainDto |} ctx
            }
            :> Task

    /// POST /rest/v1/domains (admin)
    let create (_key: AuthenticatedKey) : HttpHandler =
        Api.withJson<CreateDomainBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    match DomainAuthority.create body.Domain with
                    | Error e -> return! Problems.badRequest e ctx
                    | Ok authority ->
                        let! created = DomainRepo.create db authority
                        match created with
                        | Some d -> return! (Response.withStatusCode 201 >> Json.respond (domainDto d)) ctx
                        | None ->
                            return!
                                Problems.conflict "domain-exists" $"Domain '{authority.Value}' is already registered." ctx
                }
                :> Task)

    /// PATCH /rest/v1/domains/redirects (admin)
    let setRedirects (_key: AuthenticatedKey) : HttpHandler =
        Api.withJson<DomainRedirectsBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! domain = DomainRepo.tryGetByAuthority db (body.Domain.Trim().ToLowerInvariant())
                    match domain with
                    | None -> return! Problems.notFound $"Domain '{body.Domain}' is not registered." ctx
                    | Some d ->
                        let! _ =
                            DomainRepo.updateRedirects db (DomainId d.Id) body.BaseUrlRedirect
                                body.Regular404Redirect body.InvalidShortUrlRedirect
                        let! updated = DomainRepo.tryGetById db (DomainId d.Id)
                        return! Json.respond (domainDto updated.Value) ctx
                }
                :> Task)

    /// DELETE /rest/v1/domains/{authority} (admin)
    let delete (_key: AuthenticatedKey) : HttpHandler =
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
                    let! _ = DomainRepo.delete db (DomainId d.Id)
                    return! (Response.withStatusCode 204 >> Response.ofEmpty) ctx
            }
            :> Task

    /// GET /rest/v1/domains/{authority}/visits
    let visits (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let authority = (Request.getRoute ctx).GetString "authority"
                let! domain = DomainRepo.tryGetByAuthority db (authority.ToLowerInvariant())
                match domain with
                | None -> return! Problems.notFound $"Domain '{authority}' is not registered." ctx
                | Some d ->
                    let allowed =
                        match key.Role with
                        | ApiKeyRole.Admin -> true
                        | ApiKeyRole.Domain domainId -> domainId.Value = d.Id
                        | ApiKeyRole.Author -> false
                    if not allowed then
                        return! Problems.forbidden "This API key cannot view visits for this domain." ctx
                    else
                        let q = Request.getQuery ctx
                        let! page = VisitRepo.listForDomain db (DomainId d.Id) (Api.visitFiltersFromQuery q)
                        return! Json.respond (Api.pageDto Api.visitDto page) ctx
            }
            :> Task
