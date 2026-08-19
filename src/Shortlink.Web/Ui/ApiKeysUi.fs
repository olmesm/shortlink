namespace Shortlink.Web.Ui

open System
open System.Threading.Tasks
open Falco
open Falco.Markup
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

module ApiKeysUi =

    let private page (db: Db) (user: UiAuth.CurrentUser) (banner: XmlNode option) =
        task {
            let! keys = ApiKeyRepo.list db
            let! domains = DomainRepo.list db
            let authorityOf id =
                domains |> List.tryFind (fun d -> d.Id = id) |> Option.map (fun d -> d.Authority)
            return
                [ Elem.h1 [] [ Text.raw "API keys" ]
                  Elem.p
                      [ Attr.class' "muted" ]
                      [ Text.raw "Keys authenticate REST API calls via the X-Api-Key header. Admin keys can do everything; author keys only see short URLs they created; domain keys are limited to one domain." ]
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
                                          Elem.th [] [ Text.raw "Role" ]
                                          Elem.th [] [ Text.raw "Domain" ]
                                          Elem.th [] [ Text.raw "Status" ]
                                          Elem.th [] [ Text.raw "Expires (UTC)" ]
                                          Elem.th [] [ Text.raw "Created (UTC)" ]
                                          Elem.th [] [] ] ]
                              Elem.tbody
                                  []
                                  [ for k in keys do
                                        let expired =
                                            k.ExpiresAt |> Option.exists (fun e -> e <= DateTime.UtcNow)
                                        Elem.tr
                                            []
                                            [ Elem.td [] [ Text.enc (k.Name |> Option.defaultValue "—") ]
                                              Elem.td [] [ Elem.span [ Attr.class' "badge gray" ] [ Text.enc k.Role ] ]
                                              Elem.td
                                                  []
                                                  [ Text.enc (
                                                        k.DomainId
                                                        |> Option.bind authorityOf
                                                        |> Option.defaultValue "—") ]
                                              Elem.td
                                                  []
                                                  [ if expired then
                                                        Elem.span [ Attr.class' "badge red" ] [ Text.raw "expired" ]
                                                    elif k.Enabled then
                                                        Elem.span [ Attr.class' "badge green" ] [ Text.raw "enabled" ]
                                                    else
                                                        Elem.span [ Attr.class' "badge red" ] [ Text.raw "disabled" ] ]
                                              Elem.td
                                                  [ Attr.class' "muted" ]
                                                  [ Text.enc (k.ExpiresAt |> Option.map Format.dateTime |> Option.defaultValue "never") ]
                                              Elem.td [ Attr.class' "muted" ] [ Text.enc (Format.dateTime k.CreatedAt) ]
                                              Elem.td
                                                  [ Attr.class' "actions" ]
                                                  [ Elem.form
                                                        [ Attr.class' "inline"
                                                          Attr.method "post"
                                                          Attr.action $"/admin/api-keys/{k.Id}/toggle" ]
                                                        [ Elem.button
                                                              [ Attr.class' "secondary small" ]
                                                              [ Text.raw (if k.Enabled then "Disable" else "Enable") ] ]
                                                    Text.raw " "
                                                    Elem.form
                                                        [ Attr.class' "inline"
                                                          Attr.method "post"
                                                          Attr.action $"/admin/api-keys/{k.Id}/delete"
                                                          Attr.create "onsubmit" "return confirm('Delete this API key?')" ]
                                                        [ Elem.button [ Attr.class' "danger small" ] [ Text.raw "Delete" ] ] ] ] ] ] ]
                  Elem.h2 [] [ Text.raw "Create API key" ]
                  Elem.div
                      [ Attr.class' "card" ]
                      [ Elem.form
                            [ Attr.class' "row"; Attr.method "post"; Attr.action "/admin/api-keys" ]
                            [ Layout.field "Name" (Layout.textInput "name" "" "ci-deploy")
                              Layout.field
                                  "Role"
                                  (Elem.select
                                      [ Attr.name "role" ]
                                      [ Elem.option [ Attr.value "admin" ] [ Text.raw "admin" ]
                                        Elem.option [ Attr.value "author" ] [ Text.raw "author" ]
                                        Elem.option [ Attr.value "domain" ] [ Text.raw "domain" ] ])
                              Layout.field
                                  "Domain (for domain role)"
                                  (Elem.select
                                      [ Attr.name "domain" ]
                                      [ Elem.option [ Attr.value "" ] [ Text.raw "—" ]
                                        for d in domains do
                                            Elem.option [ Attr.value d.Authority ] [ Text.enc d.Authority ] ])
                              Layout.field
                                  "Expires (UTC, optional)"
                                  (Elem.input [ Attr.type' "datetime-local"; Attr.name "expiresAt" ])
                              Elem.div [] [ Elem.button [] [ Text.raw "Create" ] ] ] ] ]
        }

    /// GET /admin/api-keys (admin)
    let list: HttpHandler =
        UiAuth.requireAdmin (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! content = page db user None
                    return! Layout.respond user "/admin/api-keys" "API keys" content ctx
                }
                :> Task)

    /// POST /admin/api-keys (admin) — shows the plaintext key once.
    let create: HttpHandler =
        UiAuth.requireAdmin (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! form = Request.getForm ctx
                    let name =
                        match form.GetString("name", "").Trim() with
                        | "" -> None
                        | n -> Some n
                    let! domain =
                        match form.GetString("domain", "") with
                        | "" -> Task.FromResult None
                        | a -> DomainRepo.tryGetByAuthority db (a.ToLowerInvariant())
                    let role =
                        match form.GetString("role", "admin") with
                        | "author" -> Ok ApiKeyRole.Author
                        | "domain" ->
                            match domain with
                            | Some d -> Ok(ApiKeyRole.Domain(DomainId d.Id))
                            | None -> Error "Domain-role keys need a domain."
                        | _ -> Ok ApiKeyRole.Admin
                    let expiresAt =
                        match form.GetString("expiresAt", "") with
                        | "" -> None
                        | v -> Handlers.Api.tryParseDate v

                    match role with
                    | Error message ->
                        let! content = page db user (Some(Layout.alertError message))
                        return! Layout.respond user "/admin/api-keys" "API keys" content ctx
                    | Ok role ->
                        let plainKey = ApiKeys.generate ()
                        let! _ = ApiKeyRepo.insert db (ApiKeys.hash plainKey) name role expiresAt
                        let banner =
                            Layout.alertSuccess
                                [ Text.raw "API key created — copy it now, it will not be shown again: "
                                  Elem.br []
                                  Elem.strong [ Attr.class' "mono" ] [ Text.enc plainKey ] ]
                        let! content = page db user (Some banner)
                        return! Layout.respond user "/admin/api-keys" "API keys" content ctx
                }
                :> Task)

    /// POST /admin/api-keys/{id}/toggle (admin)
    let toggle: HttpHandler =
        UiAuth.requireAdmin (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = ApiKeyId((Request.getRoute ctx).GetInt64 "id")
                    let! key = ApiKeyRepo.tryGetById db id
                    match key with
                    | Some k ->
                        let! _ = ApiKeyRepo.setEnabled db id (not k.Enabled)
                        ()
                    | None -> ()
                    return! Response.redirectTemporarily "/admin/api-keys" ctx
                }
                :> Task)

    /// POST /admin/api-keys/{id}/delete (admin)
    let delete: HttpHandler =
        UiAuth.requireAdmin (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = ApiKeyId((Request.getRoute ctx).GetInt64 "id")
                    let! _ = ApiKeyRepo.delete db id
                    return! Response.redirectTemporarily "/admin/api-keys" ctx
                }
                :> Task)
