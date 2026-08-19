namespace Shortlink.Web.Handlers

open System
open System.Threading.Tasks
open Falco
open Shortlink.Data
open Shortlink.Web

type CreateApiKeyBody =
    { name: string option
      role: string option
      domain: string option
      expiresAt: DateTime option }

type PatchApiKeyBody = { enabled: bool }

module ApiApiKeys =

    let private keyDto (k: ApiKeyRow) (domainAuthority: string option) =
        {| id = k.Id
           name = k.Name
           role = k.Role
           domain = domainAuthority
           enabled = k.Enabled
           expiresAt = k.ExpiresAt
           createdAt = k.CreatedAt |}

    /// GET /rest/v1/api-keys (admin)
    let list (_key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let! keys = ApiKeyRepo.list db
                let! domains = DomainRepo.list db
                let authorityOf id =
                    domains |> List.tryFind (fun d -> d.Id = id) |> Option.map (fun d -> d.Authority)
                return!
                    Json.respond
                        {| data = keys |> List.map (fun k -> keyDto k (k.DomainId |> Option.bind authorityOf)) |}
                        ctx
            }
            :> Task

    /// POST /rest/v1/api-keys (admin) — the plaintext key is returned exactly once.
    let create (_key: ApiKeyRow) : HttpHandler =
        Api.withJson<CreateApiKeyBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let role = body.role |> Option.map (fun r -> r.ToLowerInvariant()) |> Option.defaultValue "admin"
                    if role <> "admin" && role <> "author" && role <> "domain" then
                        return! Problems.badRequest "role must be one of: admin, author, domain." ctx
                    else
                        let! domain =
                            match body.domain with
                            | Some authority -> DomainRepo.tryGetByAuthority db (authority.Trim().ToLowerInvariant())
                            | None -> Task.FromResult None
                        if role = "domain" && domain.IsNone then
                            return! Problems.badRequest "domain-role keys need an existing 'domain'." ctx
                        elif body.expiresAt |> Option.exists (fun e -> e <= DateTime.UtcNow) then
                            return! Problems.badRequest "expiresAt must be in the future." ctx
                        else
                            let plainKey = ApiKeys.generate ()
                            let! row =
                                ApiKeyRepo.insert db (ApiKeys.hash plainKey) body.name role
                                    (domain |> Option.map (fun d -> d.Id)) body.expiresAt
                            let dto = keyDto row (domain |> Option.map (fun d -> d.Authority))
                            return!
                                (Response.withStatusCode 201
                                 >> Json.respond {| dto with apiKey = plainKey |})
                                    ctx
                }
                :> Task)

    /// PATCH /rest/v1/api-keys/{id} (admin)
    let patch (_key: ApiKeyRow) : HttpHandler =
        Api.withJson<PatchApiKeyBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = (Request.getRoute ctx).GetInt64 "id"
                    let! updated = ApiKeyRepo.setEnabled db id body.enabled
                    if updated then return! Json.respond {| id = id; enabled = body.enabled |} ctx
                    else return! Problems.notFound $"API key {id} was not found." ctx
                }
                :> Task)

    /// DELETE /rest/v1/api-keys/{id} (admin)
    let delete (_key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let id = (Request.getRoute ctx).GetInt64 "id"
                let! deleted = ApiKeyRepo.delete db id
                if deleted then return! (Response.withStatusCode 204 >> Response.ofEmpty) ctx
                else return! Problems.notFound $"API key {id} was not found." ctx
            }
            :> Task
