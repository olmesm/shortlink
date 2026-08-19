namespace Shortlink.Web.Ui

open System.Threading.Tasks
open Falco
open Falco.Markup
open Shortlink.Data
open Shortlink.Web

module AuthUi =

    let private loginPage (error: string option) (returnUrl: string) : XmlNode =
        Layout.bare
            "Log in"
            [ Elem.div
                  [ Attr.class' "login-wrap" ]
                  [ Elem.div
                        [ Attr.class' "card login-card" ]
                        [ Elem.h1 [] [ Text.raw "Shortlink" ]
                          match error with
                          | Some e -> Layout.alertError e
                          | None -> Text.raw ""
                          Elem.form
                              [ Attr.class' "stack"; Attr.method "post"; Attr.action "/admin/login" ]
                              [ Elem.input [ Attr.type' "hidden"; Attr.name "returnUrl"; Attr.value returnUrl ]
                                Layout.field
                                    "Username"
                                    (Elem.input
                                        [ Attr.type' "text"; Attr.name "username"; Attr.required; Attr.autofocus ])
                                Layout.field
                                    "Password"
                                    (Elem.input [ Attr.type' "password"; Attr.name "password"; Attr.required ])
                                Elem.button [] [ Text.raw "Log in" ] ] ] ] ]

    let private safeReturnUrl (url: string) =
        if url.StartsWith "/" && not (url.StartsWith "//") then url else "/admin"

    /// GET /admin/login
    let loginForm: HttpHandler =
        fun ctx ->
            let q = Request.getQuery ctx
            let returnUrl = q.TryGetString "returnUrl" |> Option.defaultValue "/admin" |> safeReturnUrl
            match UiAuth.currentUser ctx with
            | Some _ -> Response.redirectTemporarily returnUrl ctx
            | None -> Response.ofHtml (loginPage None returnUrl) ctx

    /// POST /admin/login
    let login: HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let! form = Request.getForm ctx
                let username = form.GetString("username", "")
                let password = form.GetString("password", "")
                let returnUrl = form.GetString("returnUrl", "/admin") |> safeReturnUrl
                let! user = UserRepo.tryFindByUsername db (username.Trim())
                match user with
                | Some user when Passwords.verify password user.PasswordHash ->
                    do! UiAuth.signIn ctx user
                    return! Response.redirectTemporarily returnUrl ctx
                | _ ->
                    return!
                        (Response.withStatusCode 401
                         >> Response.ofHtml (loginPage (Some "Invalid username or password.") returnUrl))
                            ctx
            }
            :> Task

    /// POST /admin/logout
    let logout: HttpHandler =
        fun ctx ->
            task {
                do! UiAuth.signOut ctx
                return! Response.redirectTemporarily "/admin/login" ctx
            }
            :> Task
