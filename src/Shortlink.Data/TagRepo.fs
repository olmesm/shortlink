namespace Shortlink.Data

open System.Threading.Tasks
open Dapper
open Shortlink.Core

[<RequireQualifiedAccess>]
type TagRenameError =
    | TagNotFound of name: string
    | NameTaken of name: string

module TagRepo =

    let forShortUrl (db: Db) (ShortUrlId shortUrlId) : Task<string list> =
        task {
            use conn = db.CreateConnection()

            let! rows =
                conn.QueryAsync<string>(
                    """SELECT t.name FROM tags t
                       JOIN short_url_tags st ON st.tag_id = t.id
                       WHERE st.short_url_id = @id ORDER BY t.name""",
                    {| id = shortUrlId |})

            return List.ofSeq rows
        }

    /// Tags for many short URLs at once: id -> tag names.
    let forShortUrls (db: Db) (shortUrlIds: ShortUrlId list) : Task<Map<int64, string list>> =
        task {
            if shortUrlIds.IsEmpty then
                return Map.empty
            else
                use conn = db.CreateConnection()
                let inClause = db.InList("st.short_url_id", "@ids")

                let! rows =
                    conn.QueryAsync<ShortUrlTagRow>(
                        $"""SELECT st.short_url_id, t.name FROM tags t
                           JOIN short_url_tags st ON st.tag_id = t.id
                           WHERE {inClause} ORDER BY t.name""",
                        {| ids = shortUrlIds |> List.map (fun id -> id.Value) |> List.toArray |})

                return
                    rows
                    |> Seq.groupBy (fun r -> r.ShortUrlId)
                    |> Seq.map (fun (id, rs) -> id, rs |> Seq.map (fun r -> r.Name) |> List.ofSeq)
                    |> Map.ofSeq
        }

    let list (db: Db) (searchTerm: string option) (page: int) (itemsPerPage: int) : Task<Paging.Page<TagStatsRow>> =
        task {
            use conn = db.CreateConnection()
            let ilike = db.ILike("t.name", "@term")

            let whereClause, param =
                match searchTerm with
                | Some term when term <> "" ->
                    $"WHERE {ilike}",
                    {| term = "%" + term + "%"
                       offset = Paging.offset page itemsPerPage
                       limit = itemsPerPage |}
                | _ ->
                    "",
                    {| term = ""
                       offset = Paging.offset page itemsPerPage
                       limit = itemsPerPage |}

            let! total = conn.ExecuteScalarAsync<int64>($"SELECT COUNT(*) FROM tags t {whereClause}", param)

            let! rows =
                conn.QueryAsync<TagStatsRow>(
                    $"""SELECT t.id, t.name,
                              (SELECT COUNT(*) FROM short_url_tags st WHERE st.tag_id = t.id) AS short_url_count,
                              (SELECT COUNT(*) FROM visits v
                                 JOIN short_url_tags st ON st.short_url_id = v.short_url_id
                                WHERE st.tag_id = t.id) AS visit_count
                       FROM tags t {whereClause}
                       ORDER BY t.name
                       LIMIT @limit OFFSET @offset""",
                    param)

            return
                { Paging.Items = List.ofSeq rows
                  Paging.CurrentPage = page
                  Paging.ItemsPerPage = itemsPerPage
                  Paging.TotalItems = total }
        }

    /// Rename a tag; oldName is a caller-supplied candidate, newName is validated.
    let rename (db: Db) (oldName: string) (newName: TagName) : Task<Result<unit, TagRenameError>> =
        task {
            use conn = db.CreateConnection()

            let! existingNew =
                conn.ExecuteScalarAsync<int64>("SELECT COUNT(*) FROM tags WHERE name = @n", {| n = newName.Value |})

            if existingNew > 0L && oldName <> newName.Value then
                return Error(TagRenameError.NameTaken newName.Value)
            else
                let! affected =
                    conn.ExecuteAsync(
                        "UPDATE tags SET name = @n WHERE name = @o", {| n = newName.Value; o = oldName |})

                return if affected > 0 then Ok() else Error(TagRenameError.TagNotFound oldName)
        }

    /// Delete tags by candidate names; returns how many existed.
    let delete (db: Db) (names: string list) : Task<int> =
        task {
            if names.IsEmpty then
                return 0
            else
                use conn = db.CreateConnection()
                let inClause = db.InList("name", "@names")
                return! conn.ExecuteAsync($"DELETE FROM tags WHERE {inClause}", {| names = List.toArray names |})
        }

    let exists (db: Db) (name: string) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! count = conn.ExecuteScalarAsync<int64>("SELECT COUNT(*) FROM tags WHERE name = @n", {| n = name |})
            return count > 0L
        }

    let listAllNames (db: Db) : Task<string list> =
        task {
            use conn = db.CreateConnection()
            let! rows = conn.QueryAsync<string>("SELECT name FROM tags ORDER BY name")
            return List.ofSeq rows
        }
