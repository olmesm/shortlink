namespace Shortlink.Web.Handlers

open System
open System.Security.Cryptography
open System.Threading.Tasks
open Falco
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

type CreateWebhookBody =
    { name: string
      url: string
      events: string list }

type PatchWebhookBody = { enabled: bool }

module ApiWebhooks =

    let private webhookDto (w: WebhookRow) =
        {| id = w.Id
           name = w.Name
           url = w.Url
           events = w.Events.Split(',') |> Array.toList
           enabled = w.Enabled
           createdAt = w.CreatedAt |}

    /// GET /rest/v1/webhooks (admin)
    let list (_key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let! hooks = WebhookRepo.list db
                return! Json.respond {| data = hooks |> List.map webhookDto |} ctx
            }
            :> Task

    /// POST /rest/v1/webhooks (admin) — the signing secret is returned exactly once.
    let create (_key: ApiKeyRow) : HttpHandler =
        Api.withJson<CreateWebhookBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let invalidEvents =
                        body.events |> List.filter (fun e -> WebhookEvent.OfSlug e |> Option.isNone)
                    match Uri.TryCreate(body.url, UriKind.Absolute) with
                    | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps ->
                        if String.IsNullOrWhiteSpace body.name then
                            return! Problems.badRequest "name is required." ctx
                        elif body.events.IsEmpty then
                            let all = WebhookEvent.All |> List.map (fun e -> e.Slug) |> String.concat ", "
                            return! Problems.badRequest $"Subscribe to at least one event: {all}." ctx
                        elif not invalidEvents.IsEmpty then
                            let bad = String.concat ", " invalidEvents
                            let all = WebhookEvent.All |> List.map (fun e -> e.Slug) |> String.concat ", "
                            return! Problems.badRequest $"Unknown events: {bad}. Valid events: {all}." ctx
                        else
                            let secret =
                                RandomNumberGenerator.GetBytes(32)
                                |> Convert.ToHexString
                                |> fun s -> s.ToLowerInvariant()
                            let! row = WebhookRepo.insert db (body.name.Trim()) body.url secret body.events
                            return!
                                (Response.withStatusCode 201
                                 >> Json.respond {| webhookDto row with secret = secret |})
                                    ctx
                    | _ -> return! Problems.badRequest "url must be an absolute http(s) URL." ctx
                }
                :> Task)

    /// PATCH /rest/v1/webhooks/{id} (admin)
    let patch (_key: ApiKeyRow) : HttpHandler =
        Api.withJson<PatchWebhookBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = (Request.getRoute ctx).GetInt64 "id"
                    let! updated = WebhookRepo.setEnabled db id body.enabled
                    if updated then return! Json.respond {| id = id; enabled = body.enabled |} ctx
                    else return! Problems.notFound $"Webhook {id} was not found." ctx
                }
                :> Task)

    /// DELETE /rest/v1/webhooks/{id} (admin)
    let delete (_key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let id = (Request.getRoute ctx).GetInt64 "id"
                let! deleted = WebhookRepo.delete db id
                if deleted then return! (Response.withStatusCode 204 >> Response.ofEmpty) ctx
                else return! Problems.notFound $"Webhook {id} was not found." ctx
            }
            :> Task
