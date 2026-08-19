namespace Shortlink.Web.Ui

open System
open System.Security.Cryptography
open System.Threading.Tasks
open Falco
open Falco.Markup
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

module WebhooksUi =

    let private page (db: Db) (banner: XmlNode option) =
        task {
            let! hooks = WebhookRepo.list db
            return
                [ Elem.h1 [] [ Text.raw "Webhooks" ]
                  Elem.p
                      [ Attr.class' "muted" ]
                      [ Text.raw "Webhooks receive signed JSON POSTs when events happen. Payloads carry an X-Shortlink-Signature header (HMAC-SHA256 of the body with the webhook secret)." ]
                  (match banner with
                   | Some b -> b
                   | None -> Text.raw "")
                  Elem.div
                      [ Attr.class' "table-wrap" ]
                      [ Elem.table
                            []
                            [ Elem.thead
                                  []
                                  [ Elem.tr
                                        []
                                        [ Elem.th [] [ Text.raw "Name" ]
                                          Elem.th [] [ Text.raw "URL" ]
                                          Elem.th [] [ Text.raw "Events" ]
                                          Elem.th [] [ Text.raw "Status" ]
                                          Elem.th [] [] ] ]
                              Elem.tbody
                                  []
                                  [ for h in hooks do
                                        Elem.tr
                                            []
                                            [ Elem.td [] [ Text.enc h.Name ]
                                              Elem.td
                                                  []
                                                  [ Elem.span [ Attr.class' "truncate mono" ] [ Text.enc h.Url ] ]
                                              Elem.td
                                                  []
                                                  [ for e in h.Events.Split(',') do
                                                        Elem.span [ Attr.class' "badge gray" ] [ Text.enc (e.Trim()) ] ]
                                              Elem.td
                                                  []
                                                  [ if h.Enabled then
                                                        Elem.span [ Attr.class' "badge green" ] [ Text.raw "enabled" ]
                                                    else
                                                        Elem.span [ Attr.class' "badge red" ] [ Text.raw "disabled" ] ]
                                              Elem.td
                                                  [ Attr.class' "actions" ]
                                                  [ Elem.form
                                                        [ Attr.class' "inline"
                                                          Attr.method "post"
                                                          Attr.action $"/admin/webhooks/{h.Id}/toggle" ]
                                                        [ Elem.button
                                                              [ Attr.class' "secondary small" ]
                                                              [ Text.raw (if h.Enabled then "Disable" else "Enable") ] ]
                                                    Text.raw " "
                                                    Elem.form
                                                        [ Attr.class' "inline"
                                                          Attr.method "post"
                                                          Attr.action $"/admin/webhooks/{h.Id}/delete"
                                                          Attr.create "onsubmit" "return confirm('Delete this webhook?')" ]
                                                        [ Elem.button [ Attr.class' "danger small" ] [ Text.raw "Delete" ] ] ] ] ] ] ]
                  Elem.h2 [] [ Text.raw "Create webhook" ]
                  Elem.div
                      [ Attr.class' "card" ]
                      [ Elem.form
                            [ Attr.class' "stack"; Attr.method "post"; Attr.action "/admin/webhooks" ]
                            [ Elem.div
                                  [ Attr.class' "row" ]
                                  [ Layout.field "Name" (Layout.textInput "name" "" "notify-slack")
                                    Layout.field
                                        "URL"
                                        (Elem.input
                                            [ Attr.type' "url"
                                              Attr.name "url"
                                              Attr.required
                                              Attr.placeholder "https://example.com/hooks/shortlink" ]) ]
                              Elem.div
                                  []
                                  [ Elem.label [] [ Text.raw "Events" ]
                                    for e in WebhookEvent.All do
                                        // Dots in form field names would be read as nested keys.
                                        let fieldName = "event_" + e.Slug.Replace(".", "_")
                                        Layout.checkbox fieldName (e = WebhookEvent.UrlCreated) e.Slug ]
                              Elem.div [] [ Elem.button [] [ Text.raw "Create webhook" ] ] ] ] ]
        }

    /// GET /admin/webhooks (admin)
    let list: HttpHandler =
        UiAuth.requireAdmin (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! content = page db None
                    return! Layout.respond user "/admin/webhooks" "Webhooks" content ctx
                }
                :> Task)

    /// POST /admin/webhooks (admin) — shows the signing secret once.
    let create: HttpHandler =
        UiAuth.requireAdmin (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! form = Request.getForm ctx
                    let name = form.GetString("name", "").Trim()
                    let url = form.GetString("url", "").Trim()
                    let events =
                        WebhookEvent.All
                        |> List.filter (fun e ->
                            let fieldName = "event_" + e.Slug.Replace(".", "_")
                            form.GetString(fieldName, "") = "true")
                    let validUrl =
                        match Uri.TryCreate(url, UriKind.Absolute) with
                        | true, uri -> uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps
                        | _ -> false
                    if name = "" || not validUrl || events.IsEmpty then
                        let! content =
                            page db (Some(Layout.alertError "Name, a valid http(s) URL and at least one event are required."))
                        return! Layout.respond user "/admin/webhooks" "Webhooks" content ctx
                    else
                        let secret =
                            RandomNumberGenerator.GetBytes(32) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()
                        let! _ = WebhookRepo.insert db name url secret events
                        let banner =
                            Layout.alertSuccess
                                [ Text.raw "Webhook created — its signing secret (copy it now, it will not be shown again): "
                                  Elem.br []
                                  Elem.strong [ Attr.class' "mono" ] [ Text.enc secret ] ]
                        let! content = page db (Some banner)
                        return! Layout.respond user "/admin/webhooks" "Webhooks" content ctx
                }
                :> Task)

    /// POST /admin/webhooks/{id}/toggle (admin)
    let toggle: HttpHandler =
        UiAuth.requireAdmin (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = (Request.getRoute ctx).GetInt64 "id"
                    let! hooks = WebhookRepo.list db
                    match hooks |> List.tryFind (fun h -> h.Id = id) with
                    | Some h ->
                        let! _ = WebhookRepo.setEnabled db (WebhookId id) (not h.Enabled)
                        ()
                    | None -> ()
                    return! Response.redirectTemporarily "/admin/webhooks" ctx
                }
                :> Task)

    /// POST /admin/webhooks/{id}/delete (admin)
    let delete: HttpHandler =
        UiAuth.requireAdmin (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = WebhookId((Request.getRoute ctx).GetInt64 "id")
                    let! _ = WebhookRepo.delete db id
                    return! Response.redirectTemporarily "/admin/webhooks" ctx
                }
                :> Task)
