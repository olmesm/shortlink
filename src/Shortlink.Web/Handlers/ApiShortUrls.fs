namespace Shortlink.Web.Handlers

open System
open System.Text.Json.Serialization
open System.Threading.Tasks
open Falco
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

type CreateShortUrlBody =
    { longUrl: string
      customSlug: string option
      shortCodeLength: int option
      domain: string option
      title: string option
      tags: string list option
      maxVisits: int64 option
      validSince: DateTime option
      validUntil: DateTime option
      forwardQuery: bool option
      crawlable: bool option
      redirectStatus: int option
      findIfExists: bool option }

/// PATCH body: Skip = leave unchanged; explicit null clears optional fields.
type EditShortUrlBody =
    { longUrl: Skippable<string>
      title: Skippable<string option>
      tags: Skippable<string list>
      maxVisits: Skippable<int64 option>
      validSince: Skippable<DateTime option>
      validUntil: Skippable<DateTime option>
      forwardQuery: Skippable<bool>
      crawlable: Skippable<bool>
      redirectStatus: Skippable<int> }

type RuleConditionBody =
    { ``type``: string
      matchKey: string option
      matchValue: string }

type RuleBody =
    { longUrl: string
      conditions: RuleConditionBody list }

type SetRulesBody = { redirectRules: RuleBody list }

module ApiShortUrls =

    let private conditionToBody (c: RuleCondition) : RuleConditionBody =
        match c with
        | DeviceIs d -> { ``type`` = "device"; matchKey = None; matchValue = d.Slug }
        | LanguageIs l -> { ``type`` = "language"; matchKey = None; matchValue = l }
        | QueryParamIs(k, v) -> { ``type`` = "query-param"; matchKey = Some k; matchValue = v }
        | IpInRange cidr -> { ``type`` = "ip-address"; matchKey = None; matchValue = cidr }

    let private parseConditionBody (c: RuleConditionBody) : Result<RuleCondition, string> =
        match c.``type`` with
        | "device" ->
            match Device.OfSlug c.matchValue with
            | Some d -> Ok(DeviceIs d)
            | None -> Error $"Unknown device '{c.matchValue}'. Use android, ios, mobile or desktop."
        | "language" ->
            if String.IsNullOrWhiteSpace c.matchValue then Error "Language conditions need a matchValue."
            else Ok(LanguageIs(c.matchValue.Trim()))
        | "query-param" ->
            match c.matchKey with
            | Some key when key.Trim() <> "" -> Ok(QueryParamIs(key.Trim(), c.matchValue))
            | _ -> Error "Query-param conditions need a matchKey."
        | "ip-address" ->
            if String.IsNullOrWhiteSpace c.matchValue then Error "IP conditions need a matchValue (address or CIDR)."
            else Ok(IpInRange(c.matchValue.Trim()))
        | other -> Error $"Unknown condition type '{other}'. Use device, language, query-param or ip-address."

    let private createErrorResponse (error: DomainErrors.CreateShortUrlError) : HttpHandler =
        match error with
        | DomainErrors.InvalidLongUrl msg
        | DomainErrors.InvalidSlug msg -> Problems.badRequest msg
        | DomainErrors.SlugInUse(slug, domain) ->
            Problems.conflict "non-unique-slug" $"The slug '{slug}' is already in use on domain '{domain}'."
        | DomainErrors.UnknownDomain msg -> Problems.badRequest msg
        | DomainErrors.CodeGenerationExhausted ->
            Problems.problem 500 "code-generation" "Could not generate a short code"
                "Could not find a free short code; consider a larger shortCodeLength."

    /// GET /rest/v1/short-urls
    let list (key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let cfg = svc<AppConfig> ctx
                let q = Request.getQuery ctx

                // An unknown ?domain= filter matches nothing (-1 is an impossible id).
                let! domainFilter =
                    task {
                        match q.TryGetString "domain" with
                        | Some authority ->
                            let! d = DomainRepo.tryGetByAuthority db (authority.ToLowerInvariant())
                            return Some(d |> Option.map (fun d -> d.Id) |> Option.defaultValue -1L)
                        | None -> return None
                    }

                let orderBy, descending =
                    match q.TryGetString "orderBy" with
                    | Some value ->
                        let field, dir =
                            match value.Split('-') with
                            | [| f; d |] -> f, d.ToUpperInvariant()
                            | _ -> value, "ASC"
                        let order =
                            match field with
                            | "shortCode" -> ByShortCode
                            | "longUrl" -> ByLongUrl
                            | "title" -> ByTitle
                            | "visits" -> ByVisits
                            | _ -> ByDateCreated
                        order, dir = "DESC"
                    | None -> ByDateCreated, true

                let filters =
                    { ShortUrlFilters.empty with
                        SearchTerm = q.TryGetString "searchTerm"
                        Tags = q.GetStringList "tags" |> List.ofSeq
                        TagsMatchAll = (q.TryGetString "tagsMode" |> Option.map (fun m -> m.ToLowerInvariant())) = Some "all"
                        StartDate = Api.queryDate q "startDate"
                        EndDate = Api.queryDate q "endDate"
                        DomainId = domainFilter
                        ExcludeMaxVisitsReached = Api.queryBool q "excludeMaxVisitsReached"
                        ExcludePastValidUntil = Api.queryBool q "excludePastValidUntil"
                        OrderBy = orderBy
                        Descending = descending
                        Page = Api.queryInt q "page" |> Option.defaultValue 1
                        ItemsPerPage = Api.queryInt q "itemsPerPage" |> Option.defaultValue Paging.defaultPageSize }
                    |> Api.applyKeyScope key

                let! page = ShortUrlRepo.list db filters
                let! tagsByUrl = TagRepo.forShortUrls db (page.Items |> List.map (fun d -> d.Id))
                let dto =
                    Api.pageDto
                        (fun (d: ShortUrlDetail) ->
                            Services.toDto cfg (tagsByUrl.TryFind d.Id |> Option.defaultValue []) d)
                        page
                return! Json.respond dto ctx
            }
            :> Task

    /// POST /rest/v1/short-urls
    let create (key: ApiKeyRow) : HttpHandler =
        Api.withJson<CreateShortUrlBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let cfg = svc<AppConfig> ctx
                    let queues = svc<WorkQueues> ctx

                    // Domain-scoped keys may only create URLs on their domain.
                    let! domainConstraintOk =
                        task {
                            match ApiKeys.roleOf key with
                            | DomainKey domainId ->
                                let! d = DomainRepo.tryGetById db domainId
                                match d, body.domain with
                                | Some d, Some requested -> return requested.ToLowerInvariant() = d.Authority
                                | Some _, None -> return false
                                | None, _ -> return false
                            | _ -> return true
                        }

                    if not domainConstraintOk then
                        return! Problems.forbidden "This API key may only create short URLs on its own domain." ctx
                    else
                        let input =
                            { CreateShortUrlInput.make body.longUrl with
                                CustomSlug = body.customSlug
                                ShortCodeLength = body.shortCodeLength
                                Domain = body.domain
                                Title = body.title
                                Tags = body.tags |> Option.defaultValue []
                                MaxVisits = body.maxVisits
                                ValidSince = body.validSince
                                ValidUntil = body.validUntil
                                ForwardQuery = body.forwardQuery
                                Crawlable = body.crawlable
                                RedirectStatus = body.redirectStatus
                                FindIfExists = body.findIfExists |> Option.defaultValue false
                                AuthorApiKeyId = Some key.Id }

                        let! result = Services.createShortUrl db cfg queues input
                        match result with
                        | Ok dto -> return! (Response.withStatusCode 201 >> Json.respond dto) ctx
                        | Error e -> return! createErrorResponse e ctx
                }
                :> Task)

    /// GET /rest/v1/short-urls/{code}
    let get (key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let cfg = svc<AppConfig> ctx
                let code = (Request.getRoute ctx).GetString "code"
                let q = Request.getQuery ctx
                let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")
                match found with
                | Error handler -> return! handler ctx
                | Ok detail ->
                    let! tags = TagRepo.forShortUrl db detail.Id
                    return! Json.respond (Services.toDto cfg tags detail) ctx
            }
            :> Task

    /// PATCH /rest/v1/short-urls/{code}
    let edit (key: ApiKeyRow) : HttpHandler =
        Api.withJson<EditShortUrlBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let cfg = svc<AppConfig> ctx
                    let code = (Request.getRoute ctx).GetString "code"
                    let q = Request.getQuery ctx
                    let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")
                    match found with
                    | Error handler -> return! handler ctx
                    | Ok detail ->
                        let pick (skippable: Skippable<'a>) (current: 'a) =
                            match skippable with
                            | Include v -> v
                            | Skip -> current

                        let newLongUrl = pick body.longUrl detail.LongUrl
                        match Validation.validateLongUrl newLongUrl with
                        | Error e -> return! Problems.badRequest e ctx
                        | Ok newLongUrl ->
                            let newStatus =
                                let requested = pick body.redirectStatus detail.RedirectStatus
                                match RedirectStatus.OfCode requested with
                                | Some s -> s.Code
                                | None -> detail.RedirectStatus

                            let update: ShortUrlUpdate =
                                { LongUrl = newLongUrl
                                  Title =
                                    (match body.title with
                                     | Include t -> t
                                     | Skip -> detail.Title)
                                  TitleWasAutoResolved =
                                    (match body.title with
                                     | Include _ -> false
                                     | Skip -> detail.TitleWasAutoResolved)
                                  RedirectStatus = newStatus
                                  ForwardQuery = pick body.forwardQuery detail.ForwardQuery
                                  Crawlable = pick body.crawlable detail.Crawlable
                                  MaxVisits = pick body.maxVisits detail.MaxVisits
                                  ValidSince = pick body.validSince detail.ValidSince
                                  ValidUntil = pick body.validUntil detail.ValidUntil }

                            let! _ = ShortUrlRepo.update db detail.Id update

                            match body.tags with
                            | Include tags ->
                                match Validation.normalizeTags tags with
                                | Error e -> return! Problems.badRequest e ctx
                                | Ok tags ->
                                    let! tagIds = TagRepo.ensure db tags
                                    do! TagRepo.setForShortUrl db detail.Id tagIds
                                    let! updated = ShortUrlRepo.tryGetDetailById db detail.Id
                                    let! tagNames = TagRepo.forShortUrl db detail.Id
                                    return! Json.respond (Services.toDto cfg tagNames updated.Value) ctx
                            | Skip ->
                                let! updated = ShortUrlRepo.tryGetDetailById db detail.Id
                                let! tagNames = TagRepo.forShortUrl db detail.Id
                                return! Json.respond (Services.toDto cfg tagNames updated.Value) ctx
                }
                :> Task)

    /// DELETE /rest/v1/short-urls/{code}
    let delete (key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let code = (Request.getRoute ctx).GetString "code"
                let q = Request.getQuery ctx
                let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")
                match found with
                | Error handler -> return! handler ctx
                | Ok detail ->
                    let! _ = ShortUrlRepo.delete db detail.Id
                    return! (Response.withStatusCode 204 >> Response.ofEmpty) ctx
            }
            :> Task

    /// GET /rest/v1/short-urls/{code}/redirect-rules
    let getRules (key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let code = (Request.getRoute ctx).GetString "code"
                let q = Request.getQuery ctx
                let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")
                match found with
                | Error handler -> return! handler ctx
                | Ok detail ->
                    let! rules = ShortUrlRepo.getRules db detail.Id
                    let dto =
                        {| defaultLongUrl = detail.LongUrl
                           redirectRules =
                            rules
                            |> List.map (fun r ->
                                {| longUrl = r.LongUrl
                                   priority = r.Priority
                                   conditions = r.Conditions |> List.map conditionToBody |}) |}
                    return! Json.respond dto ctx
            }
            :> Task

    /// POST /rest/v1/short-urls/{code}/redirect-rules — replaces all rules.
    let setRules (key: ApiKeyRow) : HttpHandler =
        Api.withJson<SetRulesBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let code = (Request.getRoute ctx).GetString "code"
                    let q = Request.getQuery ctx
                    let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")
                    match found with
                    | Error handler -> return! handler ctx
                    | Ok detail ->
                        let parsed =
                            body.redirectRules
                            |> List.indexed
                            |> List.fold
                                (fun acc (i, rule) ->
                                    match acc with
                                    | Error e -> Error e
                                    | Ok rules ->
                                        match Validation.validateLongUrl rule.longUrl with
                                        | Error e -> Error $"Rule {i + 1}: {e}"
                                        | Ok longUrl ->
                                            let conditions =
                                                rule.conditions
                                                |> List.fold
                                                    (fun acc c ->
                                                        match acc with
                                                        | Error e -> Error e
                                                        | Ok cs ->
                                                            match parseConditionBody c with
                                                            | Ok c -> Ok(cs @ [ c ])
                                                            | Error e -> Error $"Rule {i + 1}: {e}")
                                                    (Ok [])
                                            match conditions with
                                            | Error e -> Error e
                                            | Ok [] -> Error $"Rule {i + 1} needs at least one condition."
                                            | Ok conditions ->
                                                Ok(
                                                    rules
                                                    @ [ { Priority = i + 1
                                                          LongUrl = longUrl
                                                          Conditions = conditions } ]))
                                (Ok [])
                        match parsed with
                        | Error e -> return! Problems.badRequest e ctx
                        | Ok rules ->
                            do! ShortUrlRepo.setRules db detail.Id rules
                            let dto =
                                {| defaultLongUrl = detail.LongUrl
                                   redirectRules =
                                    rules
                                    |> List.map (fun r ->
                                        {| longUrl = r.LongUrl
                                           priority = r.Priority
                                           conditions = r.Conditions |> List.map conditionToBody |}) |}
                            return! Json.respond dto ctx
                }
                :> Task)

    /// GET /rest/v1/short-urls/{code}/visits
    let listVisits (key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let code = (Request.getRoute ctx).GetString "code"
                let q = Request.getQuery ctx
                let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")
                match found with
                | Error handler -> return! handler ctx
                | Ok detail ->
                    let! page = VisitRepo.listForShortUrl db detail.Id (Api.visitFiltersFromQuery q)
                    return! Json.respond (Api.pageDto Api.visitDto page) ctx
            }
            :> Task

    /// DELETE /rest/v1/short-urls/{code}/visits
    let deleteVisits (key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let code = (Request.getRoute ctx).GetString "code"
                let q = Request.getQuery ctx
                let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")
                match found with
                | Error handler -> return! handler ctx
                | Ok detail ->
                    let! deleted = VisitRepo.deleteForShortUrl db detail.Id
                    return! Json.respond {| deletedVisits = deleted |} ctx
            }
            :> Task
