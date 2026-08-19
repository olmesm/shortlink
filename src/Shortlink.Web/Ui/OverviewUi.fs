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
                    let! series = StatsRepo.visitsPerDay db VisitScope.Global (Some start) None
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
