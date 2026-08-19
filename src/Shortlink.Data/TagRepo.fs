namespace Shortlink.Data

open System.Threading.Tasks
open Dapper
open Shortlink.Core

module TagRepo =

    /// Insert any missing tags and return the ids of all given names.
    let ensure (db: Db) (names: string list) : Task<int64 list> =
        task {
            if names.IsEmpty then
                return []
            else
                use conn = db.CreateConnection()
                for name in names do
                    let! _ =
                        conn.ExecuteAsync(
                            "INSERT INTO tags (name) VALUES (@name) ON CONFLICT (name) DO NOTHING",
                            {| name = name |})
                    ()
                let! rows =
                    conn.QueryAsync<TagRow>(
                        "SELECT id, name FROM tags WHERE name IN @names", {| names = names |})
                let byName = rows |> Seq.map (fun t -> t.Name, t.Id) |> Map.ofSeq
                return names |> List.choose byName.TryFind
        }

    /// Replace the tag set of a short URL.
    let setForShortUrl (db: Db) (shortUrlId: int64) (tagIds: int64 list) : Task<unit> =
        task {
            use conn = db.CreateConnection()
            let! _ =
                conn.ExecuteAsync(
                    "DELETE FROM short_url_tags WHERE short_url_id = @id", {| id = shortUrlId |})
            for tagId in tagIds do
                let! _ =
                    conn.ExecuteAsync(
                        """INSERT INTO short_url_tags (short_url_id, tag_id) VALUES (@s, @t)
                           ON CONFLICT (short_url_id, tag_id) DO NOTHING""",
                        {| s = shortUrlId; t = tagId |})
                ()
            return ()
        }

    let forShortUrl (db: Db) (shortUrlId: int64) : Task<string list> =
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
    let forShortUrls (db: Db) (shortUrlIds: int64 list) : Task<Map<int64, string list>> =
        task {
            if shortUrlIds.IsEmpty then
                return Map.empty
            else
                use conn = db.CreateConnection()
                let! rows =
                    conn.QueryAsync<{| ShortUrlId: int64; Name: string |}>(
                        """SELECT st.short_url_id, t.name FROM tags t
                           JOIN short_url_tags st ON st.tag_id = t.id
                           WHERE st.short_url_id IN @ids ORDER BY t.name""",
                        {| ids = shortUrlIds |})
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
                    {| term = "%" + term + "%"; offset = Paging.offset page itemsPerPage; limit = itemsPerPage |}
                | _ ->
                    "", {| term = ""; offset = Paging.offset page itemsPerPage; limit = itemsPerPage |}
            let! total =
                conn.ExecuteScalarAsync<int64>($"SELECT COUNT(*) FROM tags t {whereClause}", param)
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

    /// Rename a tag. Fails with Error when oldName is missing or newName already exists.
    let rename (db: Db) (oldName: string) (newName: string) : Task<Result<unit, string>> =
        task {
            use conn = db.CreateConnection()
            let! existingNew =
                conn.ExecuteScalarAsync<int64>(
                    "SELECT COUNT(*) FROM tags WHERE name = @n", {| n = newName |})
            if existingNew > 0L && oldName <> newName then
                return Error $"A tag named '{newName}' already exists."
            else
                let! affected =
                    conn.ExecuteAsync(
                        "UPDATE tags SET name = @n WHERE name = @o", {| n = newName; o = oldName |})
                return if affected > 0 then Ok() else Error $"Tag '{oldName}' was not found."
        }

    let delete (db: Db) (names: string list) : Task<int> =
        task {
            if names.IsEmpty then
                return 0
            else
                use conn = db.CreateConnection()
                return! conn.ExecuteAsync("DELETE FROM tags WHERE name IN @names", {| names = names |})
        }

    let exists (db: Db) (name: string) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! count =
                conn.ExecuteScalarAsync<int64>("SELECT COUNT(*) FROM tags WHERE name = @n", {| n = name |})
            return count > 0L
        }

    let listAllNames (db: Db) : Task<string list> =
        task {
            use conn = db.CreateConnection()
            let! rows = conn.QueryAsync<string>("SELECT name FROM tags ORDER BY name")
            return List.ofSeq rows
        }
