namespace Shortlink.Web.Handlers

open System
open System.Security.Cryptography
open System.Threading.Tasks
open Falco
open FsToolkit.ErrorHandling
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

type CreateWebhookBody =
    { Name: string
      Url: string
      Events: string list }

type PatchWebhookBody = { Enabled: bool }

module ApiWebhooks =

    let private webhookDto (w: WebhookRow) =
        {| Id = w.Id
           Name = w.Name
           Url = w.Url
           Events = w.Events.Split(',') |> Array.toList
           Enabled = w.Enabled
           CreatedAt = w.CreatedAt |}

    let private allEvents =
        WebhookEvent.All |> List.map (fun e -> e.Slug) |> String.concat ", "

    let private parseBody (body: CreateWebhookBody) : Result<string * string * WebhookEvent list, string> =
        result {
            do! Result.requireFalse "name is required." (String.IsNullOrWhiteSpace body.Name)

            let! _ =
                match Uri.TryCreate(body.Url, UriKind.Absolute) with
                | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> Ok uri
                | _ -> Error "url must be an absolute http(s) URL."

            do! body.Events |> Result.requireNotEmpty $"Subscribe to at least one event: {allEvents}."

            let! events =
                body.Events
                |> List.traverseResultM (fun slug ->
                    WebhookEvent.OfSlug slug
                    |> Result.requireSome $"Unknown event '{slug}'. Valid events: {allEvents}.")

            return body.Name.Trim(), body.Url, events
        }

    /// GET /rest/v1/webhooks (admin)
    let list (_key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let! hooks = WebhookRepo.list db
                return! Json.respond {| Data = hooks |> List.map webhookDto |} ctx
            }
            :> Task

    /// POST /rest/v1/webhooks (admin) — the signing secret is returned exactly once.
    let create (_key: AuthenticatedKey) : HttpHandler =
        Api.withJson<CreateWebhookBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx

                    match parseBody body with
                    | Error message -> return! Problems.badRequest message ctx
                    | Ok(name, url, events) ->
                        let secret =
                            RandomNumberGenerator.GetBytes(32)
                            |> Convert.ToHexString
                            |> fun s -> s.ToLowerInvariant()

                        let! row = WebhookRepo.insert db name url secret events
                        return! (Response.withStatusCode 201 >> Json.respond {| webhookDto row with Secret = secret |}) ctx
                }
                :> Task)

    /// PATCH /rest/v1/webhooks/{id} (admin)
    let patch (_key: AuthenticatedKey) : HttpHandler =
        Api.withJson<PatchWebhookBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = WebhookId((Request.getRoute ctx).GetInt64 "id")
                    let! updated = WebhookRepo.setEnabled db id body.Enabled

                    if updated then
                        return! Json.respond {| Id = id.Value; Enabled = body.Enabled |} ctx
                    else
                        return! Problems.notFound $"Webhook {id.Value} was not found." ctx
                }
                :> Task)

    /// DELETE /rest/v1/webhooks/{id} (admin)
    let delete (_key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let id = WebhookId((Request.getRoute ctx).GetInt64 "id")
                let! deleted = WebhookRepo.delete db id

                if deleted then
                    return! (Response.withStatusCode 204 >> Response.ofEmpty) ctx
                else
                    return! Problems.notFound $"Webhook {id.Value} was not found." ctx
            }
            :> Task
