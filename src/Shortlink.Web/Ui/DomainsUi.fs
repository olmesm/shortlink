namespace Shortlink.Web.Ui

open System.Threading.Tasks
open Falco
open Falco.Markup
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

module DomainsUi =

    /// GET /admin/domains (admin)
    let list: HttpHandler =
        UiAuth.requireAdmin (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! domains = DomainRepo.listWithStats db
                    let content =
                        [ Elem.h1 [] [ Text.raw "Domains" ]
                          Elem.p
                              [ Attr.class' "muted" ]
                              [ Text.raw "Short URLs are unique per domain. Point extra domains at this server and register them here (or let them auto-register on first use)." ]
                          Elem.div
                              [ Attr.class' "table-wrap" ]
                              [ Elem.table
                                    []
                                    [ Elem.thead
                                          []
                                          [ Elem.tr
                                                []
                                                [ Elem.th [] [ Text.raw "Domain" ]
                                                  Elem.th [] [ Text.raw "Short URLs" ]
                                                  Elem.th [] [ Text.raw "Visits" ]
                                                  Elem.th [] [ Text.raw "Not-found redirects (base / 404 / invalid)" ]
                                                  Elem.th [] [] ] ]
                                      Elem.tbody
                                          []
                                          [ for d in domains do
                                                Elem.tr
                                                    []
                                                    [ Elem.td
                                                          []
                                                          [ Elem.span [ Attr.class' "mono" ] [ Text.enc d.Authority ]
                                                            if d.IsDefault then
                                                                Text.raw " "
                                                            if d.IsDefault then
                                                                Elem.span [ Attr.class' "badge green" ] [ Text.raw "default" ] ]
                                                      Elem.td [] [ Text.raw (string d.ShortUrlCount) ]
                                                      Elem.td [] [ Text.raw (string d.VisitCount) ]
                                                      Elem.td
                                                          []
                                                          [ Elem.form
                                                                [ Attr.method "post"
                                                                  Attr.action $"/admin/domains/{d.Id}/redirects"
                                                                  Attr.class' "stack"
                                                                  Attr.style "max-width:100%" ]
                                                                [ Elem.div
                                                                      [ Attr.class' "row" ]
                                                                      [ Elem.input
                                                                            [ Attr.type' "url"
                                                                              Attr.name "baseUrlRedirect"
                                                                              Attr.placeholder "Base URL redirect"
                                                                              Attr.value (d.BaseUrlRedirect |> Option.defaultValue "") ]
                                                                        Elem.input
                                                                            [ Attr.type' "url"
                                                                              Attr.name "regular404Redirect"
                                                                              Attr.placeholder "Regular 404 redirect"
                                                                              Attr.value (d.Regular404Redirect |> Option.defaultValue "") ]
                                                                        Elem.input
                                                                            [ Attr.type' "url"
                                                                              Attr.name "invalidShortUrlRedirect"
                                                                              Attr.placeholder "Invalid short URL redirect"
                                                                              Attr.value (d.InvalidShortUrlRedirect |> Option.defaultValue "") ]
                                                                        Elem.button [ Attr.class' "secondary small" ] [ Text.raw "Save" ] ] ] ]
                                                      Elem.td
                                                          [ Attr.class' "actions" ]
                                                          [ if not d.IsDefault then
                                                                Elem.form
                                                                    [ Attr.class' "inline"
                                                                      Attr.method "post"
                                                                      Attr.action $"/admin/domains/{d.Id}/delete"
                                                                      Attr.create "onsubmit"
                                                                          "return confirm('Delete this domain and ALL its short URLs?')" ]
                                                                    [ Elem.button [ Attr.class' "danger small" ] [ Text.raw "Delete" ] ] ] ] ] ] ]
                          Elem.h2 [] [ Text.raw "Add domain" ]
                          Elem.div
                              [ Attr.class' "card" ]
                              [ Elem.form
                                    [ Attr.class' "row"; Attr.method "post"; Attr.action "/admin/domains" ]
                                    [ Layout.field "Authority (host or host:port)" (Layout.textInput "authority" "" "links.example.com")
                                      Elem.div [] [ Elem.button [] [ Text.raw "Add domain" ] ] ] ] ]
                    return! Layout.respond user "/admin/domains" "Domains" content ctx
                }
                :> Task)

    /// POST /admin/domains (admin)
    let create: HttpHandler =
        UiAuth.requireAdmin (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! form = Request.getForm ctx

                    match DomainAuthority.create (form.GetString("authority", "")) with
                    | Ok authority ->
                        let! created = DomainRepo.create db authority

                        match created with
                        | Some _ -> return! Response.redirectTemporarily "/admin/domains" ctx
                        | None ->
                            return!
                                Layout.respond user "/admin/domains" "Domains"
                                    [ Layout.alertError $"Domain '{authority.Value}' is already registered."
                                      Elem.p [] [ Elem.a [ Attr.href "/admin/domains" ] [ Text.raw "← Back to domains" ] ] ]
                                    ctx
                    | Error message ->
                        return!
                            (Response.withStatusCode 400
                             >> Layout.respond user "/admin/domains" "Domains"
                                 [ Layout.alertError message
                                   Elem.p [] [ Elem.a [ Attr.href "/admin/domains" ] [ Text.raw "← Back to domains" ] ] ])
                                ctx
                }
                :> Task)

    /// POST /admin/domains/{id}/redirects (admin)
    let setRedirects: HttpHandler =
        UiAuth.requireAdmin (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = DomainId((Request.getRoute ctx).GetInt64 "id")
                    let! form = Request.getForm ctx
                    let getOpt name =
                        match form.GetString(name, "") with
                        | "" -> None
                        | v -> Some(v.Trim())
                    let! _ =
                        DomainRepo.updateRedirects db id (getOpt "baseUrlRedirect") (getOpt "regular404Redirect")
                            (getOpt "invalidShortUrlRedirect")
                    return! Response.redirectTemporarily "/admin/domains" ctx
                }
                :> Task)

    /// POST /admin/domains/{id}/delete (admin)
    let delete: HttpHandler =
        UiAuth.requireAdmin (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = DomainId((Request.getRoute ctx).GetInt64 "id")
                    let! _ = DomainRepo.delete db id
                    return! Response.redirectTemporarily "/admin/domains" ctx
                }
                :> Task)
