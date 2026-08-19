namespace Shortlink.Web.Handlers

open System
open System.Threading.Tasks
open Falco
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

type CreateApiKeyBody =
    { Name: string option
      Role: string option
      Domain: string option
      ExpiresAt: DateTime option }

type PatchApiKeyBody = { Enabled: bool }

module ApiApiKeys =

    let private keyDto (k: ApiKeyRow) (domainAuthority: string option) =
        {| Id = k.Id
           Name = k.Name
           Role = k.Role
           Domain = domainAuthority
           Enabled = k.Enabled
           ExpiresAt = k.ExpiresAt
           CreatedAt = k.CreatedAt |}

    /// GET /rest/v1/api-keys (admin)
    let list (_key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let! keys = ApiKeyRepo.list db
                let! domains = DomainRepo.list db

                let authorityOf id =
                    domains |> List.tryFind (fun d -> d.Id = id) |> Option.map (fun d -> d.Authority)

                return!
                    Json.respond
                        {| Data = keys |> List.map (fun k -> keyDto k (k.DomainId |> Option.bind authorityOf)) |}
                        ctx
            }
            :> Task

    /// POST /rest/v1/api-keys (admin) — the plaintext key is returned exactly once.
    let create (_key: AuthenticatedKey) : HttpHandler =
        Api.withJson<CreateApiKeyBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx

                    let! domain =
                        match body.Domain with
                        | Some authority -> DomainRepo.tryGetByAuthority db (authority.Trim().ToLowerInvariant())
                        | None -> Task.FromResult None

                    let role =
                        match body.Role |> Option.map (fun r -> r.ToLowerInvariant()) with
                        | None
                        | Some "admin" -> Ok ApiKeyRole.Admin
                        | Some "author" -> Ok ApiKeyRole.Author
                        | Some "domain" ->
                            match domain with
                            | Some d -> Ok(ApiKeyRole.Domain(DomainId d.Id))
                            | None -> Error "domain-role keys need an existing 'domain'."
                        | Some other -> Error $"Unknown role '{other}'. Use admin, author or domain."

                    match role with
                    | Error message -> return! Problems.badRequest message ctx
                    | Ok _ when body.ExpiresAt |> Option.exists (fun e -> e <= DateTime.UtcNow) ->
                        return! Problems.badRequest "expiresAt must be in the future." ctx
                    | Ok role ->
                        let plainKey = ApiKeys.generate ()
                        let! row = ApiKeyRepo.insert db (ApiKeys.hash plainKey) body.Name role body.ExpiresAt
                        let dto = keyDto row (domain |> Option.map (fun d -> d.Authority))
                        return! (Response.withStatusCode 201 >> Json.respond {| dto with ApiKey = plainKey |}) ctx
                }
                :> Task)

    /// PATCH /rest/v1/api-keys/{id} (admin)
    let patch (_key: AuthenticatedKey) : HttpHandler =
        Api.withJson<PatchApiKeyBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = ApiKeyId((Request.getRoute ctx).GetInt64 "id")
                    let! updated = ApiKeyRepo.setEnabled db id body.Enabled

                    if updated then
                        return! Json.respond {| Id = id.Value; Enabled = body.Enabled |} ctx
                    else
                        return! Problems.notFound $"API key {id.Value} was not found." ctx
                }
                :> Task)

    /// DELETE /rest/v1/api-keys/{id} (admin)
    let delete (_key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let id = ApiKeyId((Request.getRoute ctx).GetInt64 "id")
                let! deleted = ApiKeyRepo.delete db id

                if deleted then
                    return! (Response.withStatusCode 204 >> Response.ofEmpty) ctx
                else
                    return! Problems.notFound $"API key {id.Value} was not found." ctx
            }
            :> Task
