namespace Shortlink.Web.Ui

open System.Threading.Tasks
open Falco
open Falco.Markup
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web
open Shortlink.Web.Handlers

module TagsUi =

    let private tagTable (page: Paging.Page<TagStatsRow>) : XmlNode =
        Elem.div
            [ Attr.id "tag-table" ]
            [ Elem.div
                  [ Attr.class' "table-wrap" ]
                  [ Elem.table
                        []
                        [ Elem.thead
                              []
                              [ Elem.tr
                                    []
                                    [ Elem.th [] [ Text.raw "Tag" ]
                                      Elem.th [] [ Text.raw "Short URLs" ]
                                      Elem.th [] [ Text.raw "Visits" ]
                                      Elem.th [] [ Text.raw "Rename" ]
                                      Elem.th [] [] ] ]
                          Elem.tbody
                              []
                              [ for t in page.Items do
                                    Elem.tr
                                        []
                                        [ Elem.td [] [ Elem.span [ Attr.class' "badge" ] [ Text.enc t.Name ] ]
                                          Elem.td
                                              []
                                              [ Elem.a
                                                    [ Attr.href $"/admin/short-urls?tag={System.Uri.EscapeDataString t.Name}" ]
                                                    [ Text.raw (string t.ShortUrlCount) ] ]
                                          Elem.td [] [ Text.raw (string t.VisitCount) ]
                                          Elem.td
                                              []
                                              [ Elem.form
                                                    [ Attr.class' "inline"; Attr.method "post"; Attr.action "/admin/tags/rename" ]
                                                    [ Elem.input [ Attr.type' "hidden"; Attr.name "oldName"; Attr.value t.Name ]
                                                      Elem.input
                                                          [ Attr.type' "text"
                                                            Attr.name "newName"
                                                            Attr.value t.Name
                                                            Attr.style "width:10rem" ]
                                                      Text.raw " "
                                                      Elem.button [ Attr.class' "secondary small" ] [ Text.raw "Rename" ] ] ]
                                          Elem.td
                                              [ Attr.class' "actions" ]
                                              [ Elem.form
                                                    [ Attr.class' "inline"
                                                      Attr.method "post"
                                                      Attr.action "/admin/tags/delete"
                                                      Attr.create "onsubmit" "return confirm('Delete this tag? Short URLs keep working.')" ]
                                                    [ Elem.input [ Attr.type' "hidden"; Attr.name "name"; Attr.value t.Name ]
                                                      Elem.button [ Attr.class' "danger small" ] [ Text.raw "Delete" ] ] ] ] ] ] ]
              Layout.pager
                  (fun p -> if p = 1 then "/admin/tags" else $"/admin/tags?page={p}")
                  page ]

    /// GET /admin/tags
    let list: HttpHandler =
        UiAuth.requireUser (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let q = Request.getQuery ctx
                    let search = q.TryGetString "search"
                    let page = Api.queryInt q "page" |> Option.defaultValue 1
                    let! result = TagRepo.list db search page 25
                    let table = tagTable result
                    if Htmx.isHtmx ctx then
                        return! Response.ofHtml table ctx
                    else
                        let content =
                            [ Elem.h1 [] [ Text.raw "Tags" ]
                              Elem.div
                                  [ Attr.class' "toolbar" ]
                                  [ Elem.form
                                        [ Htmx.hxGet "/admin/tags"
                                          Htmx.hxTarget "#tag-table"
                                          Htmx.hxSwap "outerHTML"
                                          Htmx.hxTrigger "submit, input delay:400ms from:input[name='search']"
                                          Attr.method "get"
                                          Attr.action "/admin/tags" ]
                                        [ Elem.input
                                              [ Attr.type' "search"
                                                Attr.name "search"
                                                Attr.value (search |> Option.defaultValue "")
                                                Attr.placeholder "Search tags…" ]
                                          Elem.button [ Attr.class' "secondary" ] [ Text.raw "Search" ] ] ]
                              table ]
                        return! Layout.respond user "/admin/tags" "Tags" content ctx
                }
                :> Task)

    /// POST /admin/tags/rename
    let rename: HttpHandler =
        UiAuth.requireUser (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! form = Request.getForm ctx
                    let oldName = form.GetString("oldName", "")

                    let! outcome =
                        task {
                            match TagName.create (form.GetString("newName", "")) with
                            | Error e -> return Error e
                            | Ok newName ->
                                let! renamed = TagRepo.rename db oldName newName

                                return
                                    renamed
                                    |> Result.mapError (function
                                        | TagRenameError.TagNotFound name -> $"Tag '{name}' was not found."
                                        | TagRenameError.NameTaken name -> $"A tag named '{name}' already exists.")
                        }

                    match outcome with
                    | Ok() -> return! Response.redirectTemporarily "/admin/tags" ctx
                    | Error message ->
                        let! result = TagRepo.list db None 1 25
                        let content =
                            [ Elem.h1 [] [ Text.raw "Tags" ]
                              Layout.alertError message
                              tagTable result ]
                        return!
                            (Response.withStatusCode 400
                             >> Response.ofHtml (Layout.page user "/admin/tags" "Tags" content))
                                ctx
                }
                :> Task)

    /// POST /admin/tags/delete
    let delete: HttpHandler =
        UiAuth.requireUser (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! form = Request.getForm ctx
                    let name = form.GetString("name", "")
                    if name <> "" then
                        let! _ = TagRepo.delete db [ name ]
                        ()
                    return! Response.redirectTemporarily "/admin/tags" ctx
                }
                :> Task)
