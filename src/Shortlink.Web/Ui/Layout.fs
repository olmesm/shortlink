namespace Shortlink.Web.Ui

open Falco
open Falco.Markup
open Shortlink.Web

module Layout =

    let private navLink (currentPath: string) (href: string) (label: string) =
        let isActive =
            currentPath = href
            || (href <> "/admin" && currentPath.StartsWith(href + "/"))
            || (href <> "/admin" && currentPath = href)
        Elem.a
            [ Attr.href href
              if isActive || (href = "/admin" && currentPath = "/admin") then Attr.class' "active" ]
            [ Text.enc label ]

    /// Full dashboard page shell.
    let page (user: UiAuth.CurrentUser) (currentPath: string) (title: string) (content: XmlNode list) : XmlNode =
        Elem.html
            [ Attr.lang "en" ]
            [ Elem.head
                  []
                  [ Elem.meta [ Attr.charset "utf-8" ]
                    Elem.meta [ Attr.name "viewport"; Attr.content "width=device-width, initial-scale=1" ]
                    Elem.title [] [ Text.enc $"{title} · Shortlink" ]
                    Elem.link [ Attr.rel "stylesheet"; Attr.href "/app.css" ]
                    Elem.script [ Attr.src "/htmx.min.js"; Attr.defer ] [] ]
              Elem.body
                  []
                  [ Elem.header
                        [ Attr.class' "topbar" ]
                        [ Elem.a [ Attr.class' "brand"; Attr.href "/admin" ] [ Text.raw "Shortlink" ]
                          Elem.nav
                              []
                              [ navLink currentPath "/admin" "Overview"
                                navLink currentPath "/admin/short-urls" "Short URLs"
                                navLink currentPath "/admin/tags" "Tags"
                                navLink currentPath "/admin/domains" "Domains"
                                navLink currentPath "/admin/visits/orphan" "Orphan visits"
                                if user.IsAdmin then
                                    navLink currentPath "/admin/api-keys" "API keys"
                                if user.IsAdmin then
                                    navLink currentPath "/admin/webhooks" "Webhooks"
                                if user.IsAdmin then
                                    navLink currentPath "/admin/users" "Users" ]
                          Elem.div [ Attr.class' "spacer" ] []
                          Elem.span [ Attr.class' "who" ] [ Text.enc user.Username ]
                          Elem.form
                              [ Attr.class' "inline"; Attr.method "post"; Attr.action "/admin/logout" ]
                              [ Elem.button [ Attr.class' "secondary small" ] [ Text.raw "Log out" ] ] ]
                    Elem.main [ Attr.class' "container" ] content ] ]

    /// Minimal shell for unauthenticated pages (login).
    let bare (title: string) (content: XmlNode list) : XmlNode =
        Elem.html
            [ Attr.lang "en" ]
            [ Elem.head
                  []
                  [ Elem.meta [ Attr.charset "utf-8" ]
                    Elem.meta [ Attr.name "viewport"; Attr.content "width=device-width, initial-scale=1" ]
                    Elem.title [] [ Text.enc $"{title} · Shortlink" ]
                    Elem.link [ Attr.rel "stylesheet"; Attr.href "/app.css" ] ]
              Elem.body [] content ]

    let respond (user: UiAuth.CurrentUser) (currentPath: string) (title: string) (content: XmlNode list) : HttpHandler =
        Response.ofHtml (page user currentPath title content)

    // ---- Small shared building blocks ----

    let alertError (message: string) =
        Elem.div [ Attr.class' "alert error" ] [ Text.enc message ]

    let alertSuccess (nodes: XmlNode list) =
        Elem.div [ Attr.class' "alert success" ] nodes

    let field (labelText: string) (input: XmlNode) =
        Elem.div [] [ Elem.label [] [ Text.enc labelText ]; input ]

    let textInput (name: string) (value: string) (placeholder: string) =
        Elem.input
            [ Attr.type' "text"
              Attr.name name
              Attr.value value
              if placeholder <> "" then Attr.placeholder placeholder ]

    let checkbox (name: string) (isChecked: bool) (labelText: string) =
        Elem.div
            [ Attr.class' "checkbox" ]
            [ Elem.input
                  [ Attr.type' "checkbox"
                    Attr.name name
                    Attr.id name
                    Attr.value "true"
                    if isChecked then Attr.checked' ]
              Elem.label [ Attr.for' name ] [ Text.enc labelText ] ]

    /// Pagination controls that navigate via query params on the same path.
    let pager (buildUrl: int -> string) (page: Shortlink.Core.Paging.Page<'T>) : XmlNode =
        Elem.div
            [ Attr.class' "pager" ]
            [ if page.CurrentPage > 1 then
                  Elem.a [ Attr.class' "btn secondary small"; Attr.href (buildUrl (page.CurrentPage - 1)) ] [ Text.raw "← Prev" ]
              Elem.span
                  [ Attr.class' "info" ]
                  [ Text.enc $"Page {page.CurrentPage} of {max 1 page.TotalPages} · {page.TotalItems} items" ]
              if page.CurrentPage < page.TotalPages then
                  Elem.a [ Attr.class' "btn secondary small"; Attr.href (buildUrl (page.CurrentPage + 1)) ] [ Text.raw "Next →" ] ]
