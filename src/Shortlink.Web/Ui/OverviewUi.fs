namespace Shortlink.Web.Ui

open System
open System.Threading.Tasks
open Falco
open Falco.Markup
open Shortlink.Data
open Shortlink.Web

module OverviewUi =

    let private stat (label: string) (value: string) (href: string option) =
        Elem.div
            [ Attr.class' "stat" ]
            [ Elem.div [ Attr.class' "num" ] [ Text.enc value ]
              Elem.div
                  [ Attr.class' "label" ]
                  [ match href with
                    | Some href -> Elem.a [ Attr.href href ] [ Text.enc label ]
                    | None -> Text.enc label ] ]

    /// GET /admin — dashboard overview.
    let overview: HttpHandler =
        UiAuth.requireUser (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let cfg = svc<AppConfig> ctx
                    let geo = svc<GeoIpService> ctx
                    let! o = StatsRepo.overview db
                    let start = DateTime.UtcNow.Date.AddDays(-29.0)
                    let! series = StatsRepo.visitsPerDay db GlobalVisits (Some start) None
                    let! recent =
                        ShortUrlRepo.list db { ShortUrlFilters.empty with ItemsPerPage = 5 }
                    let content =
                        [ Elem.h1 [] [ Text.raw "Overview" ]
                          (if not (cfg.DisableTracking || geo.IsAvailable || cfg.GeoLiteLicenseKey.IsSome) then
                               Elem.div
                                   [ Attr.class' "alert warning" ]
                                   [ Text.raw "Geolocation is off: set SHORTLINK_GEOLITE_LICENSE_KEY to enrich visits with country and city data." ]
                           else
                               Text.raw "")
                          Elem.div
                              [ Attr.class' "stat-grid" ]
                              [ stat "Short URLs" (string o.ShortUrlCount) (Some "/admin/short-urls")
                                stat "Visits" (string o.VisitCount) None
                                stat "Orphan visits" (string o.OrphanVisitCount) (Some "/admin/visits/orphan")
                                stat "Tags" (string o.TagCount) (Some "/admin/tags")
                                stat "Bot visits" (string o.BotVisitCount) None ]
                          Elem.div
                              [ Attr.class' "card chart-card"; Attr.style "margin-top:1rem" ]
                              [ Elem.h2 [ Attr.style "margin-top:0" ] [ Text.raw "Visits — last 30 days" ]
                                Charts.visitsPerDay series ]
                          Elem.h2 [] [ Text.raw "Latest short URLs" ]
                          Elem.div
                              [ Attr.class' "table-wrap" ]
                              [ Elem.table
                                    []
                                    [ Elem.thead
                                          []
                                          [ Elem.tr
                                                []
                                                [ Elem.th [] [ Text.raw "Short URL" ]
                                                  Elem.th [] [ Text.raw "Long URL" ]
                                                  Elem.th [] [ Text.raw "Visits" ]
                                                  Elem.th [] [ Text.raw "Created (UTC)" ] ] ]
                                      Elem.tbody
                                          []
                                          [ for d in recent.Items do
                                                Elem.tr
                                                    []
                                                    [ Elem.td
                                                          []
                                                          [ Elem.a
                                                                [ Attr.class' "mono"
                                                                  Attr.href $"/admin/short-urls/{d.Id}/edit" ]
                                                                [ Text.enc $"{d.Authority}/{d.ShortCode}" ] ]
                                                      Elem.td
                                                          []
                                                          [ Elem.span [ Attr.class' "truncate" ] [ Text.enc d.LongUrl ] ]
                                                      Elem.td [] [ Text.raw (string d.VisitCount) ]
                                                      Elem.td [ Attr.class' "muted" ] [ Text.enc (Format.dateTime d.CreatedAt) ] ] ] ] ] ]
                    return! Layout.respond user "/admin" "Overview" content ctx
                }
                :> Task)

module Routes =

    open Falco.Routing

    let endpoints: HttpEndpoint list =
        [ get "/admin" OverviewUi.overview
          get "/admin/login" AuthUi.loginForm
          post "/admin/login" AuthUi.login
          post "/admin/logout" AuthUi.logout

          get "/admin/short-urls" ShortUrlsUi.list
          get "/admin/short-urls/new" ShortUrlsUi.createFormPage
          post "/admin/short-urls/new" ShortUrlsUi.create
          get "/admin/short-urls/{id}/edit" ShortUrlsUi.editFormPage
          post "/admin/short-urls/{id}/edit" ShortUrlsUi.edit
          post "/admin/short-urls/{id}/rules/add" ShortUrlsUi.addRule
          post "/admin/short-urls/{id}/rules/delete" ShortUrlsUi.deleteRule
          post "/admin/short-urls/{id}/delete" ShortUrlsUi.deleteShortUrl
          post "/admin/short-urls/{id}/visits/delete" ShortUrlsUi.deleteVisits
          get "/admin/short-urls/{id}/visits" VisitsUi.shortUrlVisits

          get "/admin/visits/orphan" VisitsUi.orphanVisits
          post "/admin/visits/orphan/delete" VisitsUi.deleteOrphan

          get "/admin/tags" TagsUi.list
          post "/admin/tags/rename" TagsUi.rename
          post "/admin/tags/delete" TagsUi.delete

          get "/admin/domains" DomainsUi.list
          post "/admin/domains" DomainsUi.create
          post "/admin/domains/{id}/redirects" DomainsUi.setRedirects
          post "/admin/domains/{id}/delete" DomainsUi.delete

          get "/admin/api-keys" ApiKeysUi.list
          post "/admin/api-keys" ApiKeysUi.create
          post "/admin/api-keys/{id}/toggle" ApiKeysUi.toggle
          post "/admin/api-keys/{id}/delete" ApiKeysUi.delete

          get "/admin/users" UsersUi.list
          post "/admin/users" UsersUi.create
          post "/admin/users/{id}/role" UsersUi.setRole
          post "/admin/users/{id}/password" UsersUi.setPassword
          post "/admin/users/{id}/delete" UsersUi.delete

          get "/admin/webhooks" WebhooksUi.list
          post "/admin/webhooks" WebhooksUi.create
          post "/admin/webhooks/{id}/toggle" WebhooksUi.toggle
          post "/admin/webhooks/{id}/delete" WebhooksUi.delete ]
