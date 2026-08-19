namespace Shortlink.Web.Handlers

open System
open System.Text.Json.Serialization
open System.Threading.Tasks
open Falco
open FsToolkit.ErrorHandling
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

type CreateShortUrlBody =
    { LongUrl: string
      CustomSlug: string option
      ShortCodeLength: int option
      Domain: string option
      Title: string option
      Tags: string list option
      MaxVisits: int64 option
      ValidSince: DateTime option
      ValidUntil: DateTime option
      ForwardQuery: bool option
      Crawlable: bool option
      RedirectStatus: int option
      FindIfExists: bool option }

/// PATCH body: Skip = leave unchanged; explicit null clears optional fields.
type EditShortUrlBody =
    { LongUrl: Skippable<string>
      Title: Skippable<string option>
      Tags: Skippable<string list>
      MaxVisits: Skippable<int64 option>
      ValidSince: Skippable<DateTime option>
      ValidUntil: Skippable<DateTime option>
      ForwardQuery: Skippable<bool>
      Crawlable: Skippable<bool>
      RedirectStatus: Skippable<int> }

type RuleConditionBody =
    { Type: string
      MatchKey: string option
      MatchValue: string }

type RuleBody =
    { LongUrl: string
      Conditions: RuleConditionBody list }

type SetRulesBody = { RedirectRules: RuleBody list }

module ApiShortUrls =

    let private conditionToBody (c: RuleCondition) : RuleConditionBody =
        match c with
        | DeviceIs d -> { Type = "device"; MatchKey = None; MatchValue = d.Slug }
        | LanguageIs l -> { Type = "language"; MatchKey = None; MatchValue = l }
        | QueryParamIs(k, v) -> { Type = "query-param"; MatchKey = Some k; MatchValue = v }
        | IpInRange cidr -> { Type = "ip-address"; MatchKey = None; MatchValue = cidr }

    let private parseConditionBody (c: RuleConditionBody) : Result<RuleCondition, string> =
        match c.Type with
        | "device" ->
            Device.OfSlug c.MatchValue
            |> Option.map DeviceIs
            |> Result.requireSome $"Unknown device '{c.MatchValue}'. Use android, ios, mobile or desktop."
        | "language" ->
            if String.IsNullOrWhiteSpace c.MatchValue then Error "Language conditions need a matchValue."
            else Ok(LanguageIs(c.MatchValue.Trim()))
        | "query-param" ->
            match c.MatchKey with
            | Some key when key.Trim() <> "" -> Ok(QueryParamIs(key.Trim(), c.MatchValue))
            | _ -> Error "Query-param conditions need a matchKey."
        | "ip-address" ->
            if String.IsNullOrWhiteSpace c.MatchValue then
                Error "IP conditions need a matchValue (address or CIDR)."
            else
                Ok(IpInRange(c.MatchValue.Trim()))
        | other -> Error $"Unknown condition type '{other}'. Use device, language, query-param or ip-address."

    let private parseRule (index: int) (rule: RuleBody) : Result<RedirectRule, string> =
        result {
            let! longUrl =
                LongUrl.create rule.LongUrl |> Result.mapError (fun e -> $"Rule {index + 1}: {e}")

            let! conditions =
                rule.Conditions
                |> List.traverseResultM parseConditionBody
                |> Result.mapError (fun e -> $"Rule {index + 1}: {e}")

            do! conditions |> Result.requireNotEmpty $"Rule {index + 1} needs at least one condition."

            return
                { Priority = index + 1
                  LongUrl = longUrl.Value
                  Conditions = conditions }
        }

    let private respondError (error: ShortUrlError) : HttpHandler =
        match error with
        | ShortUrlError.SlugInUse _ -> Problems.conflict "non-unique-slug" error.Message
        | ShortUrlError.CodeGenerationExhausted ->
            Problems.problem 500 "code-generation" "Could not generate a short code" error.Message
        | _ -> Problems.badRequest error.Message

    /// GET /rest/v1/short-urls
    let list (key: AuthenticatedKey) : HttpHandler =
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
                            return Some(DomainId(d |> Option.map (fun d -> d.Id) |> Option.defaultValue -1L))
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
                            | "shortCode" -> ShortUrlOrder.ShortCode
                            | "longUrl" -> ShortUrlOrder.LongUrl
                            | "title" -> ShortUrlOrder.Title
                            | "visits" -> ShortUrlOrder.Visits
                            | _ -> ShortUrlOrder.DateCreated

                        order, dir = "DESC"
                    | None -> ShortUrlOrder.DateCreated, true

                let filters =
                    { ShortUrlFilters.empty with
                        SearchTerm = q.TryGetString "searchTerm"
                        Tags = q.GetStringList "tags" |> List.ofSeq
                        TagsMatchAll =
                            (q.TryGetString "tagsMode" |> Option.map (fun m -> m.ToLowerInvariant())) = Some "all"
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
                let! tagsByUrl = TagRepo.forShortUrls db (page.Items |> List.map (fun d -> ShortUrlId d.Id))

                let dto =
                    Api.pageDto
                        (fun (d: ShortUrlDetail) -> Dto.shortUrl cfg (tagsByUrl.TryFind d.Id |> Option.defaultValue []) d)
                        page

                return! Json.respond dto ctx
            }
            :> Task

    /// POST /rest/v1/short-urls
    let create (key: AuthenticatedKey) : HttpHandler =
        Api.withJson<CreateShortUrlBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    let cfg = svc<AppConfig> ctx
                    let queues = svc<WorkQueues> ctx

                    // Domain-scoped keys may only create URLs on their own domain.
                    let! domainConstraintOk =
                        task {
                            match key.Role with
                            | ApiKeyRole.Domain domainId ->
                                let! d = DomainRepo.tryGetById db domainId

                                match d, body.Domain with
                                | Some d, Some requested -> return requested.ToLowerInvariant() = d.Authority
                                | _ -> return false
                            | ApiKeyRole.Admin
                            | ApiKeyRole.Author -> return true
                        }

                    if not domainConstraintOk then
                        return! Problems.forbidden "This API key may only create short URLs on its own domain." ctx
                    else
                        let spec =
                            ShortUrlSpec.create
                                { LongUrl = body.LongUrl
                                  CustomSlug = body.CustomSlug
                                  CodeLength = body.ShortCodeLength
                                  Domain = body.Domain
                                  Title = body.Title
                                  Tags = body.Tags |> Option.defaultValue []
                                  ValidSince = body.ValidSince
                                  ValidUntil = body.ValidUntil
                                  MaxVisits = body.MaxVisits
                                  RedirectStatus = body.RedirectStatus
                                  ForwardQuery = body.ForwardQuery
                                  Crawlable = body.Crawlable
                                  FindIfExists = body.FindIfExists |> Option.defaultValue false }

                        match spec with
                        | Error e -> return! respondError e ctx
                        | Ok spec ->
                            let! result =
                                Services.createShortUrl db cfg queues (Some(Choice2Of2 key.Id)) spec

                            match result with
                            | Ok dto -> return! (Response.withStatusCode 201 >> Json.respond dto) ctx
                            | Error e -> return! respondError e ctx
                }
                :> Task)

    /// GET /rest/v1/short-urls/{code}
    let get (key: AuthenticatedKey) : HttpHandler =
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
                    let! tags = TagRepo.forShortUrl db (ShortUrlId detail.Id)
                    return! Json.respond (Dto.shortUrl cfg tags detail) ctx
            }
            :> Task

    /// PATCH /rest/v1/short-urls/{code} — merge with current state, then
    /// validate the merged result as a whole through the edit spec.
    let edit (key: AuthenticatedKey) : HttpHandler =
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

                        let edit =
                            ShortUrlEdit.create
                                { LongUrl = pick body.LongUrl detail.LongUrl
                                  Title = pick body.Title detail.Title
                                  ValidSince = pick body.ValidSince detail.ValidSince
                                  ValidUntil = pick body.ValidUntil detail.ValidUntil
                                  MaxVisits = pick body.MaxVisits detail.MaxVisits
                                  RedirectStatus = pick body.RedirectStatus detail.RedirectStatus
                                  ForwardQuery = pick body.ForwardQuery detail.ForwardQuery
                                  Crawlable = pick body.Crawlable detail.Crawlable
                                  Tags =
                                    match body.Tags with
                                    | Include tags -> Some tags
                                    | Skip -> None }

                        match edit with
                        | Error e -> return! respondError e ctx
                        | Ok edit ->
                            let! dto = Services.editShortUrl db cfg (ShortUrlId detail.Id) detail edit
                            return! Json.respond dto ctx
                }
                :> Task)

    /// DELETE /rest/v1/short-urls/{code}
    let delete (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let code = (Request.getRoute ctx).GetString "code"
                let q = Request.getQuery ctx
                let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")

                match found with
                | Error handler -> return! handler ctx
                | Ok detail ->
                    let! _ = ShortUrlRepo.delete db (ShortUrlId detail.Id)
                    return! (Response.withStatusCode 204 >> Response.ofEmpty) ctx
            }
            :> Task

    let private rulesDto (detail: ShortUrlDetail) (rules: RedirectRule list) =
        {| DefaultLongUrl = detail.LongUrl
           RedirectRules =
            rules
            |> List.map (fun r ->
                {| LongUrl = r.LongUrl
                   Priority = r.Priority
                   Conditions = r.Conditions |> List.map conditionToBody |}) |}

    /// GET /rest/v1/short-urls/{code}/redirect-rules
    let getRules (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let code = (Request.getRoute ctx).GetString "code"
                let q = Request.getQuery ctx
                let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")

                match found with
                | Error handler -> return! handler ctx
                | Ok detail ->
                    let! rules = ShortUrlRepo.getRules db (ShortUrlId detail.Id)
                    return! Json.respond (rulesDto detail rules) ctx
            }
            :> Task

    /// POST /rest/v1/short-urls/{code}/redirect-rules — replaces all rules.
    let setRules (key: AuthenticatedKey) : HttpHandler =
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
                            body.RedirectRules
                            |> List.indexed
                            |> List.traverseResultM (fun (i, rule) -> parseRule i rule)

                        match parsed with
                        | Error e -> return! Problems.badRequest e ctx
                        | Ok rules ->
                            do! ShortUrlRepo.setRules db (ShortUrlId detail.Id) rules
                            return! Json.respond (rulesDto detail rules) ctx
                }
                :> Task)

    /// GET /rest/v1/short-urls/{code}/visits
    let listVisits (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let code = (Request.getRoute ctx).GetString "code"
                let q = Request.getQuery ctx
                let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")

                match found with
                | Error handler -> return! handler ctx
                | Ok detail ->
                    let! page = VisitRepo.listForShortUrl db (ShortUrlId detail.Id) (Api.visitFiltersFromQuery q)
                    return! Json.respond (Api.pageDto Api.visitDto page) ctx
            }
            :> Task

    /// DELETE /rest/v1/short-urls/{code}/visits
    let deleteVisits (key: AuthenticatedKey) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let code = (Request.getRoute ctx).GetString "code"
                let q = Request.getQuery ctx
                let! found = Api.findAccessibleShortUrl db key code (q.TryGetString "domain")

                match found with
                | Error handler -> return! handler ctx
                | Ok detail ->
                    let! deleted = VisitRepo.deleteForShortUrl db (ShortUrlId detail.Id)
                    return! Json.respond {| DeletedVisits = deleted |} ctx
            }
            :> Task
