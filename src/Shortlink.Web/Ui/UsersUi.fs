namespace Shortlink.Web.Ui

open System.Threading.Tasks
open Falco
open Falco.Markup
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

module UsersUi =

    let private page (db: Db) (currentUser: UiAuth.CurrentUser) (banner: XmlNode option) =
        task {
            let! users = UserRepo.list db
            let! adminCount = UserRepo.countAdmins db
            return
                [ Elem.h1 [] [ Text.raw "Users" ]
                  Elem.p
                      [ Attr.class' "muted" ]
                      [ Text.raw "Dashboard accounts. Admins additionally manage domains, API keys, webhooks and users." ]
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
                                        [ Elem.th [] [ Text.raw "Username" ]
                                          Elem.th [] [ Text.raw "Role" ]
                                          Elem.th [] [ Text.raw "Created (UTC)" ]
                                          Elem.th [] [ Text.raw "Set new password" ]
                                          Elem.th [] [] ] ]
                              Elem.tbody
                                  []
                                  [ for u in users do
                                        let isSelf = UserId u.Id = currentUser.Id
                                        let isLastAdmin = u.Role = UserRole.Admin.Slug && adminCount <= 1L
                                        Elem.tr
                                            []
                                            [ Elem.td
                                                  []
                                                  [ Text.enc u.Username
                                                    if isSelf then Text.raw " "
                                                    if isSelf then Elem.span [ Attr.class' "badge gray" ] [ Text.raw "you" ] ]
                                              Elem.td
                                                  []
                                                  [ Elem.form
                                                        [ Attr.class' "inline"
                                                          Attr.method "post"
                                                          Attr.action $"/admin/users/{u.Id}/role" ]
                                                        [ Elem.select
                                                              [ Attr.name "role"
                                                                if isLastAdmin then Attr.disabled ]
                                                              [ Elem.option
                                                                    [ Attr.value "user"
                                                                      if u.Role = "user" then Attr.selected ]
                                                                    [ Text.raw "user" ]
                                                                Elem.option
                                                                    [ Attr.value "admin"
                                                                      if u.Role = "admin" then Attr.selected ]
                                                                    [ Text.raw "admin" ] ]
                                                          Text.raw " "
                                                          if not isLastAdmin then
                                                              Elem.button [ Attr.class' "secondary small" ] [ Text.raw "Set" ] ] ]
                                              Elem.td [ Attr.class' "muted" ] [ Text.enc (Format.dateTime u.CreatedAt) ]
                                              Elem.td
                                                  []
                                                  [ Elem.form
                                                        [ Attr.class' "inline"
                                                          Attr.method "post"
                                                          Attr.action $"/admin/users/{u.Id}/password" ]
                                                        [ Elem.input
                                                              [ Attr.type' "password"
                                                                Attr.name "password"
                                                                Attr.placeholder "New password"
                                                                Attr.style "width:11rem" ]
                                                          Text.raw " "
                                                          Elem.button [ Attr.class' "secondary small" ] [ Text.raw "Update" ] ] ]
                                              Elem.td
                                                  [ Attr.class' "actions" ]
                                                  [ if not isSelf && not isLastAdmin then
                                                        Elem.form
                                                            [ Attr.class' "inline"
                                                              Attr.method "post"
                                                              Attr.action $"/admin/users/{u.Id}/delete"
                                                              Attr.create "onsubmit" "return confirm('Delete this user?')" ]
                                                            [ Elem.button [ Attr.class' "danger small" ] [ Text.raw "Delete" ] ] ] ] ] ] ]
                  Elem.h2 [] [ Text.raw "Create user" ]
                  Elem.div
                      [ Attr.class' "card" ]
                      [ Elem.form
                            [ Attr.class' "row"; Attr.method "post"; Attr.action "/admin/users" ]
                            [ Layout.field "Username" (Layout.textInput "username" "" "")
                              Layout.field
                                  "Password"
                                  (Elem.input [ Attr.type' "password"; Attr.name "password"; Attr.required ])
                              Layout.field
                                  "Role"
                                  (Elem.select
                                      [ Attr.name "role" ]
                                      [ Elem.option [ Attr.value "user" ] [ Text.raw "user" ]
                                        Elem.option [ Attr.value "admin" ] [ Text.raw "admin" ] ])
                              Elem.div [] [ Elem.button [] [ Text.raw "Create user" ] ] ] ] ]
        }

    /// GET /admin/users (admin)
    let list: HttpHandler =
        UiAuth.requireAdmin (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! content = page db user None
                    return! Layout.respond user "/admin/users" "Users" content ctx
                }
                :> Task)

    /// POST /admin/users (admin)
    let create: HttpHandler =
        UiAuth.requireAdmin (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! form = Request.getForm ctx
                    let username = form.GetString("username", "").Trim()
                    let password = form.GetString("password", "")
                    let role =
                        if form.GetString("role", "user") = "admin" then UserRole.Admin else UserRole.Regular
                    if username = "" || password.Length < 8 then
                        let! content =
                            page db user (Some(Layout.alertError "Username is required and the password needs at least 8 characters."))
                        return! Layout.respond user "/admin/users" "Users" content ctx
                    else
                        let! created = UserRepo.insert db username (Passwords.hash password) role
                        match created with
                        | Some _ -> return! Response.redirectTemporarily "/admin/users" ctx
                        | None ->
                            let! content =
                                page db user (Some(Layout.alertError $"Username '{username}' is already taken."))
                            return! Layout.respond user "/admin/users" "Users" content ctx
                }
                :> Task)

    /// POST /admin/users/{id}/role (admin)
    let setRole: HttpHandler =
        UiAuth.requireAdmin (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = UserId((Request.getRoute ctx).GetInt64 "id")
                    let! form = Request.getForm ctx
                    let role =
                        if form.GetString("role", "user") = "admin" then UserRole.Admin else UserRole.Regular
                    let! target = UserRepo.tryFindById db id
                    let! adminCount = UserRepo.countAdmins db
                    let demotingLastAdmin (u: UserRow) =
                        u.Role = UserRole.Admin.Slug && role = UserRole.Regular && adminCount <= 1L
                    match target with
                    | Some u when not (demotingLastAdmin u) ->
                        let! _ = UserRepo.updateRole db id role
                        ()
                    | _ -> ()
                    return! Response.redirectTemporarily "/admin/users" ctx
                }
                :> Task)

    /// POST /admin/users/{id}/password (admin)
    let setPassword: HttpHandler =
        UiAuth.requireAdmin (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = UserId((Request.getRoute ctx).GetInt64 "id")
                    let! form = Request.getForm ctx
                    let password = form.GetString("password", "")
                    if password.Length >= 8 then
                        let! _ = UserRepo.updatePassword db id (Passwords.hash password)
                        return! Response.redirectTemporarily "/admin/users" ctx
                    else
                        let! content = page db user (Some(Layout.alertError "Passwords need at least 8 characters."))
                        return! Layout.respond user "/admin/users" "Users" content ctx
                }
                :> Task)

    /// POST /admin/users/{id}/delete (admin)
    let delete: HttpHandler =
        UiAuth.requireAdmin (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let id = UserId((Request.getRoute ctx).GetInt64 "id")
                    let! target = UserRepo.tryFindById db id
                    let! adminCount = UserRepo.countAdmins db
                    match target with
                    | Some u when UserId u.Id <> user.Id && not (u.Role = UserRole.Admin.Slug && adminCount <= 1L) ->
                        let! _ = UserRepo.delete db id
                        ()
                    | _ -> ()
                    return! Response.redirectTemporarily "/admin/users" ctx
                }
                :> Task)
