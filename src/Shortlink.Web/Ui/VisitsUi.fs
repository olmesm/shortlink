namespace Shortlink.Web.Ui

open System
open System.Threading.Tasks
open Falco
open Falco.Markup
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web
open Shortlink.Web.Handlers

module VisitsUi =

    let private visitTable (page: Paging.Page<VisitRow>) (buildUrl: int -> string) : XmlNode =
        Elem.div
            []
            [ Elem.div
                  [ Attr.class' "table-wrap" ]
                  [ Elem.table
                        []
                        [ Elem.thead
                              []
                              [ Elem.tr
                                    []
                                    [ Elem.th [] [ Text.raw "When (UTC)" ]
                                      Elem.th [] [ Text.raw "Location" ]
                                      Elem.th [] [ Text.raw "Browser / OS" ]
                                      Elem.th [] [ Text.raw "Device" ]
                                      Elem.th [] [ Text.raw "Referrer" ]
                                      Elem.th [] [ Text.raw "Bot?" ] ] ]
                          Elem.tbody
                              []
                              [ for v in page.Items do
                                    Elem.tr
                                        []
                                        [ Elem.td [ Attr.class' "muted" ] [ Text.enc (Format.dateTime v.VisitedAt) ]
                                          Elem.td
                                              []
                                              [ Text.enc (
                                                    match v.City, v.CountryName with
                                                    | Some city, Some country -> $"{city}, {country}"
                                                    | None, Some country -> country
                                                    | _ -> "—") ]
                                          Elem.td
                                              []
                                              [ Text.enc (
                                                    match v.Browser, v.Os with
                                                    | Some b, Some o -> $"{b} / {o}"
                                                    | Some b, None -> b
                                                    | None, Some o -> o
                                                    | None, None -> "—") ]
                                          Elem.td [] [ Text.enc (v.Device |> Option.defaultValue "—") ]
                                          Elem.td
                                              []
                                              [ match v.Referer with
                                                | Some r ->
                                                    Elem.span [ Attr.class' "truncate"; Attr.style "max-width:220px" ] [ Text.enc r ]
                                                | None -> Text.raw "—" ]
                                          Elem.td
                                              []
                                              [ if v.IsBot then Elem.span [ Attr.class' "badge red" ] [ Text.raw "bot" ]
                                                else Text.raw "" ] ] ] ] ]
              Layout.pager buildUrl page ]

    let private rangeForm (action: string) (startDate: string) (endDate: string) =
        Elem.form
            [ Attr.class' "toolbar"; Attr.method "get"; Attr.action action ]
            [ Elem.div
                  []
                  [ Elem.label [] [ Text.raw "From (UTC)" ]
                    Elem.input [ Attr.type' "datetime-local"; Attr.name "startDate"; Attr.value startDate ] ]
              Elem.div
                  []
                  [ Elem.label [] [ Text.raw "To (UTC)" ]
                    Elem.input [ Attr.type' "datetime-local"; Attr.name "endDate"; Attr.value endDate ] ]
              Elem.button [ Attr.class' "secondary" ] [ Text.raw "Apply" ] ]

    let private breakdownCard (db: Db) (scope: VisitScope) (startDate: DateTime option) (endDate: DateTime option) (title: string) (column: string) =
        task {
            let! rows = StatsRepo.breakdown db scope column startDate endDate 8
            return
                Elem.div
                    [ Attr.class' "card" ]
                    [ Elem.h2 [ Attr.style "margin-top:0" ] [ Text.enc title ]
                      Charts.barList (rows |> List.map (fun (label, count) -> (label |> Option.defaultValue "Unknown"), count)) ]
        }

    /// Shared analytics block: chart + breakdowns + visit table.
    let analyticsContent
        (db: Db)
        (scope: VisitScope)
        (listVisits: VisitFilters -> Task<Paging.Page<VisitRow>>)
        (basePath: string)
        (q: RequestData)
        : Task<XmlNode list> =
        task {
            let startDate = Api.queryDate q "startDate"
            let endDate = Api.queryDate q "endDate"
            let filters =
                { VisitFilters.empty with
                    StartDate = startDate
                    EndDate = endDate
                    Page = Api.queryInt q "page" |> Option.defaultValue 1
                    ItemsPerPage = 25 }

            let defaultedStart =
                startDate |> Option.defaultValue (DateTime.UtcNow.Date.AddDays(-29.0))
            let! series = StatsRepo.visitsPerDay db scope (Some defaultedStart) endDate
            let! byCountry = breakdownCard db scope startDate endDate "Countries" "country_name"
            let! byBrowser = breakdownCard db scope startDate endDate "Browsers" "browser"
            let! byOs = breakdownCard db scope startDate endDate "Operating systems" "os"
            let! byReferer = breakdownCard db scope startDate endDate "Referrers" "referer"
            let! page = listVisits filters

            let query (p: int) =
                let parts =
                    [ match q.TryGetString "startDate" with
                      | Some s when s <> "" -> $"startDate={Uri.EscapeDataString s}"
                      | _ -> ()
                      match q.TryGetString "endDate" with
                      | Some s when s <> "" -> $"endDate={Uri.EscapeDataString s}"
                      | _ -> ()
                      if p > 1 then $"page={p}" ]
                match parts with
                | [] -> basePath
                | parts -> basePath + "?" + String.Join("&", parts)

            return
                [ rangeForm basePath (q.TryGetString "startDate" |> Option.defaultValue "") (q.TryGetString "endDate" |> Option.defaultValue "")
                  Elem.div
                      [ Attr.class' "card chart-card" ]
                      [ Elem.h2 [ Attr.style "margin-top:0" ] [ Text.raw "Visits per day" ]
                        Charts.visitsPerDay series ]
                  Elem.div [ Attr.class' "split" ] [ byCountry; byBrowser; byOs; byReferer ]
                  Elem.h2 [] [ Text.raw "Visits" ]
                  visitTable page query ]
        }

    /// GET /admin/short-urls/{id}/visits
    let shortUrlVisits: HttpHandler =
        UiAuth.requireUser (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let cfg = svc<AppConfig> ctx
                    let id = (Request.getRoute ctx).GetInt64 "id"
                    let! detail = ShortUrlRepo.tryGetDetailById db id
                    match detail with
                    | None -> return! (Response.withStatusCode 404 >> Response.ofPlainText "Not found") ctx
                    | Some detail ->
                        let q = Request.getQuery ctx
                        let! content =
                            analyticsContent db (ShortUrlVisits detail.Id)
                                (fun f -> VisitRepo.listForShortUrl db detail.Id f)
                                $"/admin/short-urls/{detail.Id}/visits" q
                        let header =
                            [ Elem.h1
                                  []
                                  [ Text.raw "Visits — "
                                    Elem.span [ Attr.class' "mono" ] [ Text.enc $"{detail.Authority}/{detail.ShortCode}" ] ]
                              Elem.p
                                  []
                                  [ Elem.a [ Attr.href $"/admin/short-urls/{detail.Id}/edit" ] [ Text.raw "← Back to edit" ]
                                    Text.raw " · "
                                    Elem.a
                                        [ Attr.href (Services.shortUrlFor cfg detail.Authority detail.ShortCode)
                                          Attr.target "_blank"
                                          Attr.rel "noreferrer" ]
                                        [ Text.enc detail.LongUrl ] ] ]
                        return! Layout.respond user "/admin/short-urls" "Visits" (header @ content) ctx
                }
                :> Task)

    /// GET /admin/visits/orphan
    let orphanVisits: HttpHandler =
        UiAuth.requireUser (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let q = Request.getQuery ctx
                    let visitType = q.TryGetString "type" |> Option.bind VisitType.OfSlug
                    let! content =
                        analyticsContent db OrphanVisits
                            (fun f -> VisitRepo.listOrphan db visitType f)
                            "/admin/visits/orphan" q
                    let header =
                        [ Elem.h1 [] [ Text.raw "Orphan visits" ]
                          Elem.p
                              [ Attr.class' "muted" ]
                              [ Text.raw "Traffic that reached this server without hitting an active short URL: base URL hits, unknown short codes and other 404s." ]
                          Elem.div
                              [ Attr.class' "toolbar" ]
                              [ Elem.a [ Attr.class' "btn secondary small"; Attr.href "/admin/visits/orphan" ] [ Text.raw "All" ]
                                Elem.a
                                    [ Attr.class' "btn secondary small"; Attr.href "/admin/visits/orphan?type=base_url" ]
                                    [ Text.raw "Base URL" ]
                                Elem.a
                                    [ Attr.class' "btn secondary small"
                                      Attr.href "/admin/visits/orphan?type=invalid_short_url" ]
                                    [ Text.raw "Invalid short URLs" ]
                                Elem.a
                                    [ Attr.class' "btn secondary small"; Attr.href "/admin/visits/orphan?type=regular_404" ]
                                    [ Text.raw "Other 404s" ]
                                (if user.IsAdmin then
                                     Elem.form
                                         [ Attr.class' "inline"
                                           Attr.method "post"
                                           Attr.action "/admin/visits/orphan/delete"
                                           Attr.create "onsubmit" "return confirm('Delete ALL orphan visits?')" ]
                                         [ Elem.button [ Attr.class' "danger small" ] [ Text.raw "Delete all orphan visits" ] ]
                                 else
                                     Text.raw "") ] ]
                    return! Layout.respond user "/admin/visits/orphan" "Orphan visits" (header @ content) ctx
                }
                :> Task)

    /// POST /admin/visits/orphan/delete (admin)
    let deleteOrphan: HttpHandler =
        UiAuth.requireAdmin (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! _ = VisitRepo.deleteOrphan db
                    return! Response.redirectTemporarily "/admin/visits/orphan" ctx
                }
                :> Task)
