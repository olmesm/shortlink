namespace Shortlink.Web.Handlers

open System.Threading.Tasks
open Falco
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

type RenameTagBody = { oldName: string; newName: string }

module ApiTags =

    /// GET /rest/v1/tags?withStats=true&searchTerm=&page=&itemsPerPage=
    let list (_key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let q = Request.getQuery ctx
                let withStats = Api.queryBool q "withStats"
                let page = Api.queryInt q "page" |> Option.defaultValue 1
                let itemsPerPage = Api.queryInt q "itemsPerPage" |> Option.defaultValue Paging.maxPageSize
                let! result = TagRepo.list db (q.TryGetString "searchTerm") page itemsPerPage
                if withStats then
                    let dto =
                        Api.pageDto
                            (fun (t: TagStatsRow) ->
                                {| tag = t.Name
                                   shortUrlsCount = t.ShortUrlCount
                                   visitsCount = t.VisitCount |})
                            result
                    return! Json.respond dto ctx
                else
                    let dto = Api.pageDto (fun (t: TagStatsRow) -> t.Name) result
                    return! Json.respond dto ctx
            }
            :> Task

    /// PUT /rest/v1/tags — rename
    let rename (_key: ApiKeyRow) : HttpHandler =
        Api.withJson<RenameTagBody> (fun body ->
            fun ctx ->
                task {
                    let db = svc<Db> ctx
                    match Validation.normalizeTag body.newName with
                    | Error e -> return! Problems.badRequest e ctx
                    | Ok newName ->
                        let! result = TagRepo.rename db body.oldName newName
                        match result with
                        | Ok() -> return! Json.respond {| oldName = body.oldName; newName = newName |} ctx
                        | Error e when e.Contains "was not found" -> return! Problems.notFound e ctx
                        | Error e -> return! Problems.conflict "tag-conflict" e ctx
                }
                :> Task)

    /// DELETE /rest/v1/tags?tags[]=a&tags[]=b (also accepts tags=a,b)
    let delete (_key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let q = Request.getQuery ctx
                let tags =
                    let listed = q.GetStringList "tags" |> List.ofSeq
                    listed |> List.collect (fun t -> t.Split(',') |> List.ofArray)
                    |> List.map (fun t -> t.Trim())
                    |> List.filter (fun t -> t <> "")
                if tags.IsEmpty then
                    return! Problems.badRequest "Provide at least one tag to delete via ?tags[]=." ctx
                else
                    let! deleted = TagRepo.delete db tags
                    return! Json.respond {| deletedTags = deleted |} ctx
            }
            :> Task

    /// GET /rest/v1/tags/{tag}/visits
    let visits (_key: ApiKeyRow) : HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let tag = (Request.getRoute ctx).GetString "tag"
                let! exists = TagRepo.exists db tag
                if not exists then
                    return! Problems.notFound $"Tag '{tag}' was not found." ctx
                else
                    let q = Request.getQuery ctx
                    let! page = VisitRepo.listForTag db tag (Api.visitFiltersFromQuery q)
                    return! Json.respond (Api.pageDto Api.visitDto page) ctx
            }
            :> Task
