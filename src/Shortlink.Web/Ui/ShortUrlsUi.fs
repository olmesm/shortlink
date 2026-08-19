namespace Shortlink.Web.Ui

open System
open System.Globalization
open System.Threading.Tasks
open Falco
open Falco.Markup
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web
open Shortlink.Web.Handlers

module ShortUrlsUi =

    // ---- helpers ----

    let private parseDateLocal (s: string) : DateTime option =
        match DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, d -> Some(DateTime.SpecifyKind(d, DateTimeKind.Utc))
        | _ -> None

    let private dateLocalValue (d: DateTime option) =
        match d with
        | Some d -> d.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)
        | None -> ""

    type ListQuery =
        { Search: string
          Tag: string
          Domain: string
          OrderBy: string
          Dir: string
          Page: int }

    let private readListQuery (q: RequestData) : ListQuery =
        { Search = q.TryGetString "search" |> Option.defaultValue ""
          Tag = q.TryGetString "tag" |> Option.defaultValue ""
          Domain = q.TryGetString "domain" |> Option.defaultValue ""
          OrderBy = q.TryGetString "orderBy" |> Option.defaultValue "dateCreated"
          Dir = q.TryGetString "dir" |> Option.defaultValue "desc"
          Page = Api.queryInt q "page" |> Option.defaultValue 1 }

    let private listUrl (lq: ListQuery) (page: int) =
        let parts =
            [ if lq.Search <> "" then $"search={Uri.EscapeDataString lq.Search}"
              if lq.Tag <> "" then $"tag={Uri.EscapeDataString lq.Tag}"
              if lq.Domain <> "" then $"domain={Uri.EscapeDataString lq.Domain}"
              if lq.OrderBy <> "dateCreated" then $"orderBy={lq.OrderBy}"
              if lq.Dir <> "desc" then $"dir={lq.Dir}"
              if page > 1 then $"page={page}" ]
        match parts with
        | [] -> "/admin/short-urls"
        | parts -> "/admin/short-urls?" + String.Join("&", parts)

    let private filtersOf (lq: ListQuery) : ShortUrlFilters =
        { ShortUrlFilters.empty with
            SearchTerm = (if lq.Search = "" then None else Some lq.Search)
            Tags = (if lq.Tag = "" then [] else [ lq.Tag ])
            OrderBy =
                (match lq.OrderBy with
                 | "shortCode" -> ByShortCode
                 | "longUrl" -> ByLongUrl
                 | "title" -> ByTitle
                 | "visits" -> ByVisits
                 | _ -> ByDateCreated)
            Descending = lq.Dir <> "asc"
            Page = lq.Page
            ItemsPerPage = 20 }

    // ---- list ----

    let private sortHeader (lq: ListQuery) (field: string) (label: string) =
        let nextDir = if lq.OrderBy = field && lq.Dir = "desc" then "asc" else "desc"
        let url = listUrl { lq with OrderBy = field; Dir = nextDir; Page = 1 } 1
        let marker =
            if lq.OrderBy = field then (if lq.Dir = "asc" then " ↑" else " ↓") else ""
        Elem.th
            []
            [ Elem.a
                  [ Attr.href url
                    Htmx.hxGet url
                    Htmx.hxTarget "#su-table"
                    Htmx.hxSwap "outerHTML"
                    Htmx.hxPushUrl ]
                  [ Text.enc (label + marker) ] ]

    let private urlTable
        (cfg: AppConfig)
        (lq: ListQuery)
        (page: Paging.Page<ShortUrlDetail>)
        (tagsByUrl: Map<int64, string list>)
        : XmlNode =
        Elem.div
            [ Attr.id "su-table" ]
            [ Elem.div
                  [ Attr.class' "table-wrap" ]
                  [ Elem.table
                        []
                        [ Elem.thead
                              []
                              [ Elem.tr
                                    []
                                    [ sortHeader lq "shortCode" "Short URL"
                                      sortHeader lq "title" "Title"
                                      sortHeader lq "longUrl" "Long URL"
                                      Elem.th [] [ Text.raw "Tags" ]
                                      sortHeader lq "visits" "Visits"
                                      sortHeader lq "dateCreated" "Created"
                                      Elem.th [] [] ] ]
                          Elem.tbody
                              []
                              [ for d in page.Items do
                                    let shortUrl = Services.shortUrlFor cfg d.Authority d.ShortCode
                                    Elem.tr
                                        []
                                        [ Elem.td
                                              []
                                              [ Elem.a
                                                    [ Attr.class' "mono"
                                                      Attr.href shortUrl
                                                      Attr.target "_blank"
                                                      Attr.rel "noreferrer" ]
                                                    [ Text.enc $"{d.Authority}/{d.ShortCode}" ] ]
                                          Elem.td
                                              []
                                              [ Elem.span
                                                    [ Attr.class' "truncate"; Attr.style "max-width:200px" ]
                                                    [ Text.enc (d.Title |> Option.defaultValue "—") ] ]
                                          Elem.td
                                              []
                                              [ Elem.a
                                                    [ Attr.class' "truncate"
                                                      Attr.href d.LongUrl
                                                      Attr.target "_blank"
                                                      Attr.rel "noreferrer" ]
                                                    [ Text.enc d.LongUrl ] ]
                                          Elem.td
                                              []
                                              [ for t in tagsByUrl.TryFind d.Id |> Option.defaultValue [] do
                                                    Elem.span [ Attr.class' "badge" ] [ Text.enc t ] ]
                                          Elem.td
                                              []
                                              [ Elem.a
                                                    [ Attr.href $"/admin/short-urls/{d.Id}/visits" ]
                                                    [ Text.raw (Format.count d.VisitCount) ] ]
                                          Elem.td [ Attr.class' "muted" ] [ Text.enc (Format.dateTime d.CreatedAt) ]
                                          Elem.td
                                              [ Attr.class' "actions" ]
                                              [ Elem.a
                                                    [ Attr.class' "btn secondary small"
                                                      Attr.href $"/admin/short-urls/{d.Id}/edit" ]
                                                    [ Text.raw "Edit" ] ] ] ] ] ]
              Layout.pager (fun p -> listUrl lq p) page ]

    /// GET /admin/short-urls (full page or htmx fragment)
    let list: HttpHandler =
        UiAuth.requireUser (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let cfg = svc<AppConfig> ctx
                    let q = Request.getQuery ctx
                    let lq = readListQuery q
                    let! page = ShortUrlRepo.list db (filtersOf lq)
                    let! tagsByUrl = TagRepo.forShortUrls db (page.Items |> List.map (fun d -> d.Id))
                    let table = urlTable cfg lq page tagsByUrl
                    if Htmx.isHtmx ctx then
                        return! Response.ofHtml table ctx
                    else
                        let! allTags = TagRepo.listAllNames db
                        let content =
                            [ Elem.h1 [] [ Text.raw "Short URLs" ]
                              Elem.div
                                  [ Attr.class' "toolbar" ]
                                  [ Elem.form
                                        [ Htmx.hxGet "/admin/short-urls"
                                          Htmx.hxTarget "#su-table"
                                          Htmx.hxSwap "outerHTML"
                                          Htmx.hxTrigger "submit, input delay:400ms from:input[name='search'], change from:select"
                                          Htmx.hxPushUrl
                                          Attr.method "get"
                                          Attr.action "/admin/short-urls" ]
                                        [ Elem.input
                                              [ Attr.type' "search"
                                                Attr.name "search"
                                                Attr.value lq.Search
                                                Attr.placeholder "Search code, URL, title or tag…" ]
                                          Elem.select
                                              [ Attr.name "tag" ]
                                              [ Elem.option [ Attr.value "" ] [ Text.raw "All tags" ]
                                                for t in allTags do
                                                    Elem.option
                                                        [ Attr.value t
                                                          if t = lq.Tag then Attr.selected ]
                                                        [ Text.enc t ] ]
                                          Elem.button [ Attr.class' "secondary" ] [ Text.raw "Filter" ] ]
                                    Elem.a [ Attr.class' "btn"; Attr.href "/admin/short-urls/new" ] [ Text.raw "+ New short URL" ] ]
                              table ]
                        return! Layout.respond user "/admin/short-urls" "Short URLs" content ctx
                }
                :> Task)

    // ---- create ----

    let private redirectStatusSelect (current: int) =
        Elem.select
            [ Attr.name "redirectStatus" ]
            [ for status, label in
                  [ 301, "301 — permanent"
                    302, "302 — found (default)"
                    307, "307 — temporary, keep method"
                    308, "308 — permanent, keep method" ] do
                  Elem.option
                      [ Attr.value (string status)
                        if status = current then Attr.selected ]
                      [ Text.enc label ] ]

    let private createForm (cfg: AppConfig) (error: string option) (values: Map<string, string>) : XmlNode list =
        let v name = values.TryFind name |> Option.defaultValue ""
        [ Elem.h1 [] [ Text.raw "New short URL" ]
          match error with
          | Some e -> Layout.alertError e
          | None -> Text.raw ""
          Elem.div
              [ Attr.class' "card" ]
              [ Elem.form
                    [ Attr.class' "stack"; Attr.method "post"; Attr.action "/admin/short-urls/new" ]
                    [ Layout.field
                          "Long URL *"
                          (Elem.input
                              [ Attr.type' "url"
                                Attr.name "longUrl"
                                Attr.value (v "longUrl")
                                Attr.required
                                Attr.placeholder "https://example.com/some/very/long/path" ])
                      Elem.div
                          [ Attr.class' "row" ]
                          [ Layout.field "Custom slug (optional)" (Layout.textInput "customSlug" (v "customSlug") "my-campaign")
                            Layout.field
                                "Domain (optional)"
                                (Layout.textInput "domain" (v "domain") cfg.DefaultDomain) ]
                      Layout.field "Title (optional; auto-resolved when empty)" (Layout.textInput "title" (v "title") "")
                      Layout.field "Tags (comma separated)" (Layout.textInput "tags" (v "tags") "marketing, launch")
                      Elem.div
                          [ Attr.class' "row" ]
                          [ Layout.field
                                "Valid since (UTC)"
                                (Elem.input
                                    [ Attr.type' "datetime-local"; Attr.name "validSince"; Attr.value (v "validSince") ])
                            Layout.field
                                "Valid until (UTC)"
                                (Elem.input
                                    [ Attr.type' "datetime-local"; Attr.name "validUntil"; Attr.value (v "validUntil") ])
                            Layout.field
                                "Max visits"
                                (Elem.input
                                    [ Attr.type' "number"; Attr.name "maxVisits"; Attr.value (v "maxVisits"); Attr.min "1" ]) ]
                      Layout.field "Redirect status" (redirectStatusSelect 302)
                      Layout.checkbox "forwardQuery" true "Forward query params to the long URL"
                      Layout.checkbox "crawlable" false "Allow search engines to crawl this short URL"
                      Elem.div [] [ Elem.button [] [ Text.raw "Create short URL" ] ] ] ] ]

    /// GET /admin/short-urls/new
    let createFormPage: HttpHandler =
        UiAuth.requireUser (fun user ->
            fun ctx ->
                let cfg = svc<AppConfig> ctx
                Layout.respond user "/admin/short-urls" "New short URL" (createForm cfg None Map.empty) ctx)

    /// POST /admin/short-urls/new
    let create: HttpHandler =
        UiAuth.requireUser (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let cfg = svc<AppConfig> ctx
                    let queues = svc<WorkQueues> ctx
                    let! form = Request.getForm ctx
                    let get name = form.GetString(name, "")
                    let getOpt name =
                        match get name with
                        | "" -> None
                        | v -> Some v

                    let input =
                        { CreateShortUrlInput.make (get "longUrl") with
                            CustomSlug = getOpt "customSlug"
                            Domain = getOpt "domain"
                            Title = getOpt "title"
                            Tags =
                                (get "tags").Split(',')
                                |> Array.map (fun t -> t.Trim())
                                |> Array.filter (fun t -> t <> "")
                                |> Array.toList
                            MaxVisits = getOpt "maxVisits" |> Option.bind (fun v -> match Int64.TryParse v with | true, n when n > 0L -> Some n | _ -> None)
                            ValidSince = getOpt "validSince" |> Option.bind parseDateLocal
                            ValidUntil = getOpt "validUntil" |> Option.bind parseDateLocal
                            ForwardQuery = Some(get "forwardQuery" = "true")
                            Crawlable = Some(get "crawlable" = "true")
                            RedirectStatus = getOpt "redirectStatus" |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None)
                            AuthorUserId = Some user.Id }

                    let! result = Services.createShortUrl db cfg queues input
                    match result with
                    | Ok _ -> return! Response.redirectTemporarily "/admin/short-urls" ctx
                    | Error e ->
                        let message =
                            match e with
                            | DomainErrors.InvalidLongUrl m
                            | DomainErrors.InvalidSlug m
                            | DomainErrors.UnknownDomain m -> m
                            | DomainErrors.SlugInUse(slug, domain) ->
                                $"The slug '{slug}' is already in use on domain '{domain}'."
                            | DomainErrors.CodeGenerationExhausted ->
                                "Could not find a free short code; try again or use a custom slug."
                        let values =
                            [ "longUrl"; "customSlug"; "domain"; "title"; "tags"; "validSince"; "validUntil"; "maxVisits" ]
                            |> List.map (fun name -> name, get name)
                            |> Map.ofList
                        return!
                            (Response.withStatusCode 400
                             >> Response.ofHtml (Layout.page user "/admin/short-urls" "New short URL" (createForm cfg (Some message) values)))
                                ctx
                }
                :> Task)

    // ---- edit ----

    let private conditionLabel (c: RuleCondition) =
        match c with
        | DeviceIs d -> $"Device is {d.Slug}"
        | LanguageIs l -> $"Language matches {l}"
        | QueryParamIs(k, v) -> $"Query param {k} = {v}"
        | IpInRange cidr -> $"IP in {cidr}"

    let private editPage
        (cfg: AppConfig)
        (user: UiAuth.CurrentUser)
        (detail: ShortUrlDetail)
        (tags: string list)
        (rules: RedirectRule list)
        (banner: XmlNode option)
        : XmlNode list =
        let shortUrl = Services.shortUrlFor cfg detail.Authority detail.ShortCode
        [ Elem.h1 [] [ Text.raw "Edit short URL" ]
          (match banner with
           | Some b -> b
           | None -> Text.raw "")
          Elem.div
              [ Attr.class' "card" ]
              [ Elem.p
                    []
                    [ Elem.a
                          [ Attr.class' "mono"; Attr.href shortUrl; Attr.target "_blank"; Attr.rel "noreferrer" ]
                          [ Text.enc shortUrl ]
                      Text.raw " · "
                      Elem.a
                          [ Attr.href $"/{detail.ShortCode}/qr-code?size=300"; Attr.target "_blank" ]
                          [ Text.raw "QR code" ]
                      Text.raw " · "
                      Elem.a [ Attr.href $"/admin/short-urls/{detail.Id}/visits" ] [ Text.enc $"{detail.VisitCount} visits" ] ]
                Elem.form
                    [ Attr.class' "stack"; Attr.method "post"; Attr.action $"/admin/short-urls/{detail.Id}/edit" ]
                    [ Layout.field
                          "Long URL *"
                          (Elem.input
                              [ Attr.type' "url"; Attr.name "longUrl"; Attr.value detail.LongUrl; Attr.required ])
                      Layout.field "Title" (Layout.textInput "title" (detail.Title |> Option.defaultValue "") "")
                      Layout.field "Tags (comma separated)" (Layout.textInput "tags" (String.Join(", ", tags)) "")
                      Elem.div
                          [ Attr.class' "row" ]
                          [ Layout.field
                                "Valid since (UTC)"
                                (Elem.input
                                    [ Attr.type' "datetime-local"
                                      Attr.name "validSince"
                                      Attr.value (dateLocalValue detail.ValidSince) ])
                            Layout.field
                                "Valid until (UTC)"
                                (Elem.input
                                    [ Attr.type' "datetime-local"
                                      Attr.name "validUntil"
                                      Attr.value (dateLocalValue detail.ValidUntil) ])
                            Layout.field
                                "Max visits"
                                (Elem.input
                                    [ Attr.type' "number"
                                      Attr.name "maxVisits"
                                      Attr.value (detail.MaxVisits |> Option.map string |> Option.defaultValue "")
                                      Attr.min "1" ]) ]
                      Layout.field "Redirect status" (redirectStatusSelect detail.RedirectStatus)
                      Layout.checkbox "forwardQuery" detail.ForwardQuery "Forward query params to the long URL"
                      Layout.checkbox "crawlable" detail.Crawlable "Allow search engines to crawl this short URL"
                      Elem.div
                          []
                          [ Elem.button [] [ Text.raw "Save changes" ] ] ] ]
          Elem.h2 [] [ Text.raw "Conditional redirect rules" ]
          Elem.div
              [ Attr.class' "card" ]
              [ Elem.p
                    [ Attr.class' "muted" ]
                    [ Text.raw "Rules are evaluated top-down; the first rule whose conditions all match overrides the long URL." ]
                (if rules.IsEmpty then
                     Elem.p [ Attr.class' "muted" ] [ Text.raw "No rules configured." ]
                 else
                     Elem.div
                         [ Attr.class' "table-wrap" ]
                         [ Elem.table
                               []
                               [ Elem.thead
                                     []
                                     [ Elem.tr
                                           []
                                           [ Elem.th [] [ Text.raw "#" ]
                                             Elem.th [] [ Text.raw "Conditions" ]
                                             Elem.th [] [ Text.raw "Target URL" ]
                                             Elem.th [] [] ] ]
                                 Elem.tbody
                                     []
                                     [ for rule in rules do
                                           Elem.tr
                                               []
                                               [ Elem.td [] [ Text.raw (string rule.Priority) ]
                                                 Elem.td
                                                     []
                                                     [ Elem.ul
                                                           [ Attr.class' "rule-conditions" ]
                                                           [ for c in rule.Conditions do
                                                                 Elem.li [] [ Text.enc (conditionLabel c) ] ] ]
                                                 Elem.td
                                                     []
                                                     [ Elem.span [ Attr.class' "truncate" ] [ Text.enc rule.LongUrl ] ]
                                                 Elem.td
                                                     [ Attr.class' "actions" ]
                                                     [ Elem.form
                                                           [ Attr.class' "inline"
                                                             Attr.method "post"
                                                             Attr.action $"/admin/short-urls/{detail.Id}/rules/delete" ]
                                                           [ Elem.input
                                                                 [ Attr.type' "hidden"
                                                                   Attr.name "priority"
                                                                   Attr.value (string rule.Priority) ]
                                                             Elem.button
                                                                 [ Attr.class' "danger small" ]
                                                                 [ Text.raw "Remove" ] ] ] ] ] ] ])
                Elem.h2 [] [ Text.raw "Add rule" ]
                Elem.form
                    [ Attr.class' "stack"; Attr.method "post"; Attr.action $"/admin/short-urls/{detail.Id}/rules/add" ]
                    [ Layout.field
                          "Target long URL *"
                          (Elem.input [ Attr.type' "url"; Attr.name "ruleLongUrl"; Attr.required ])
                      Elem.div
                          [ Attr.class' "row" ]
                          [ Layout.field
                                "Device"
                                (Elem.select
                                    [ Attr.name "device" ]
                                    [ Elem.option [ Attr.value "" ] [ Text.raw "Any device" ]
                                      Elem.option [ Attr.value "android" ] [ Text.raw "Android" ]
                                      Elem.option [ Attr.value "ios" ] [ Text.raw "iOS" ]
                                      Elem.option [ Attr.value "mobile" ] [ Text.raw "Any mobile" ]
                                      Elem.option [ Attr.value "desktop" ] [ Text.raw "Desktop" ] ])
                            Layout.field "Language (e.g. en or en-GB)" (Layout.textInput "language" "" "")
                            Layout.field "IP address / CIDR" (Layout.textInput "ipAddress" "" "10.0.0.0/8") ]
                      Elem.div
                          [ Attr.class' "row" ]
                          [ Layout.field "Query param name" (Layout.textInput "queryKey" "" "utm_source")
                            Layout.field "Query param value" (Layout.textInput "queryValue" "" "newsletter") ]
                      Elem.div [] [ Elem.button [ Attr.class' "secondary" ] [ Text.raw "Add rule" ] ] ] ]
          Elem.h2 [] [ Text.raw "Danger zone" ]
          Elem.div
              [ Attr.class' "card" ]
              [ Elem.form
                    [ Attr.class' "inline"
                      Attr.method "post"
                      Attr.action $"/admin/short-urls/{detail.Id}/delete"
                      Attr.create "onsubmit" "return confirm('Delete this short URL and all its visits?')" ]
                    [ Elem.button [ Attr.class' "danger" ] [ Text.raw "Delete short URL" ] ]
                Text.raw " "
                Elem.form
                    [ Attr.class' "inline"
                      Attr.method "post"
                      Attr.action $"/admin/short-urls/{detail.Id}/visits/delete"
                      Attr.create "onsubmit" "return confirm('Delete all visits of this short URL?')" ]
                    [ Elem.button [ Attr.class' "danger" ] [ Text.raw "Delete its visits" ] ] ] ]

    let private loadDetail (db: Db) (ctx: Microsoft.AspNetCore.Http.HttpContext) =
        let id = (Request.getRoute ctx).GetInt64 "id"
        ShortUrlRepo.tryGetDetailById db id

    /// GET /admin/short-urls/{id}/edit
    let editFormPage: HttpHandler =
        UiAuth.requireUser (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let cfg = svc<AppConfig> ctx
                    let! detail = loadDetail db ctx
                    match detail with
                    | None -> return! (Response.withStatusCode 404 >> Response.ofPlainText "Not found") ctx
                    | Some detail ->
                        let! tags = TagRepo.forShortUrl db detail.Id
                        let! rules = ShortUrlRepo.getRules db detail.Id
                        return!
                            Layout.respond user "/admin/short-urls" "Edit short URL"
                                (editPage cfg user detail tags rules None) ctx
                }
                :> Task)

    /// POST /admin/short-urls/{id}/edit
    let edit: HttpHandler =
        UiAuth.requireUser (fun user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let cfg = svc<AppConfig> ctx
                    let! detail = loadDetail db ctx
                    match detail with
                    | None -> return! (Response.withStatusCode 404 >> Response.ofPlainText "Not found") ctx
                    | Some detail ->
                        let! form = Request.getForm ctx
                        let get name = form.GetString(name, "")
                        let getOpt name =
                            match get name with
                            | "" -> None
                            | v -> Some v

                        match Validation.validateLongUrl (get "longUrl") with
                        | Error e ->
                            let! tags = TagRepo.forShortUrl db detail.Id
                            let! rules = ShortUrlRepo.getRules db detail.Id
                            return!
                                (Response.withStatusCode 400
                                 >> Response.ofHtml (
                                     Layout.page user "/admin/short-urls" "Edit short URL"
                                         (editPage cfg user detail tags rules (Some(Layout.alertError e)))))
                                    ctx
                        | Ok longUrl ->
                            let newTitle = getOpt "title"
                            let update: ShortUrlUpdate =
                                { LongUrl = longUrl
                                  Title = newTitle
                                  TitleWasAutoResolved =
                                    (newTitle = detail.Title && detail.TitleWasAutoResolved)
                                  RedirectStatus =
                                    getOpt "redirectStatus"
                                    |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None)
                                    |> Option.bind (RedirectStatus.OfCode >> Option.map (fun s -> s.Code))
                                    |> Option.defaultValue detail.RedirectStatus
                                  ForwardQuery = get "forwardQuery" = "true"
                                  Crawlable = get "crawlable" = "true"
                                  MaxVisits =
                                    getOpt "maxVisits"
                                    |> Option.bind (fun v -> match Int64.TryParse v with | true, n when n > 0L -> Some n | _ -> None)
                                  ValidSince = getOpt "validSince" |> Option.bind parseDateLocal
                                  ValidUntil = getOpt "validUntil" |> Option.bind parseDateLocal }
                            let! _ = ShortUrlRepo.update db detail.Id update

                            match Validation.normalizeTags ((get "tags").Split(',') |> Array.filter (fun t -> t.Trim() <> "")) with
                            | Ok tags ->
                                let! tagIds = TagRepo.ensure db tags
                                do! TagRepo.setForShortUrl db detail.Id tagIds
                            | Error _ -> ()

                            return! Response.redirectTemporarily $"/admin/short-urls/{detail.Id}/edit" ctx
                }
                :> Task)

    /// POST /admin/short-urls/{id}/rules/add
    let addRule: HttpHandler =
        UiAuth.requireUser (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! detail = loadDetail db ctx
                    match detail with
                    | None -> return! (Response.withStatusCode 404 >> Response.ofPlainText "Not found") ctx
                    | Some detail ->
                        let! form = Request.getForm ctx
                        let get name = form.GetString(name, "")
                        let conditions =
                            [ match Device.OfSlug(get "device") with
                              | Some d -> yield DeviceIs d
                              | None -> ()
                              if (get "language").Trim() <> "" then
                                  yield LanguageIs((get "language").Trim())
                              if (get "queryKey").Trim() <> "" then
                                  yield QueryParamIs((get "queryKey").Trim(), (get "queryValue").Trim())
                              if (get "ipAddress").Trim() <> "" then
                                  yield IpInRange((get "ipAddress").Trim()) ]
                        match Validation.validateLongUrl (get "ruleLongUrl"), conditions with
                        | Ok target, (_ :: _) ->
                            let! rules = ShortUrlRepo.getRules db detail.Id
                            let newRule =
                                { Priority = rules.Length + 1
                                  LongUrl = target
                                  Conditions = conditions }
                            do! ShortUrlRepo.setRules db detail.Id (rules @ [ newRule ])
                        | _ -> ()
                        return! Response.redirectTemporarily $"/admin/short-urls/{detail.Id}/edit" ctx
                }
                :> Task)

    /// POST /admin/short-urls/{id}/rules/delete
    let deleteRule: HttpHandler =
        UiAuth.requireUser (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! detail = loadDetail db ctx
                    match detail with
                    | None -> return! (Response.withStatusCode 404 >> Response.ofPlainText "Not found") ctx
                    | Some detail ->
                        let! form = Request.getForm ctx
                        let priority = form.GetInt32("priority", -1)
                        let! rules = ShortUrlRepo.getRules db detail.Id
                        let remaining = rules |> List.filter (fun r -> r.Priority <> priority)
                        do! ShortUrlRepo.setRules db detail.Id remaining
                        return! Response.redirectTemporarily $"/admin/short-urls/{detail.Id}/edit" ctx
                }
                :> Task)

    /// POST /admin/short-urls/{id}/delete
    let deleteShortUrl: HttpHandler =
        UiAuth.requireUser (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! detail = loadDetail db ctx
                    match detail with
                    | Some detail ->
                        let! _ = ShortUrlRepo.delete db detail.Id
                        ()
                    | None -> ()
                    return! Response.redirectTemporarily "/admin/short-urls" ctx
                }
                :> Task)

    /// POST /admin/short-urls/{id}/visits/delete
    let deleteVisits: HttpHandler =
        UiAuth.requireUser (fun _user ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let! detail = loadDetail db ctx
                    match detail with
                    | None -> return! (Response.withStatusCode 404 >> Response.ofPlainText "Not found") ctx
                    | Some detail ->
                        let! _ = VisitRepo.deleteForShortUrl db detail.Id
                        return! Response.redirectTemporarily $"/admin/short-urls/{detail.Id}/edit" ctx
                }
                :> Task)
