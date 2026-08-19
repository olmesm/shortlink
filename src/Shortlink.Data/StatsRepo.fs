namespace Shortlink.Data

open System
open System.Threading.Tasks
open Dapper
open Shortlink.Core

/// Which set of visits a stats query aggregates over.
[<RequireQualifiedAccess>]
type VisitScope =
    | Global
    | ShortUrl of ShortUrlId
    | Tag of tagName: string
    | Domain of DomainId
    | Orphan

[<CLIMutable>]
type DayCountRow = { Day: string; Count: int64 }

[<CLIMutable>]
type OverviewRow =
    { ShortUrlCount: int64
      VisitCount: int64
      OrphanVisitCount: int64
      TagCount: int64
      BotVisitCount: int64 }

module StatsRepo =

    let private scopeWhere (scope: VisitScope) (p: DynamicParameters) : string =
        match scope with
        | VisitScope.Global -> Sql.isValidVisit "vi"
        | VisitScope.ShortUrl(ShortUrlId id) ->
            p.Add("scopeShortUrlId", id)
            $"""{Sql.isValidVisit "vi"} AND vi.short_url_id = @scopeShortUrlId"""
        | VisitScope.Tag name ->
            p.Add("scopeTagName", name)

            $"""{Sql.isValidVisit "vi"} AND EXISTS (
                 SELECT 1 FROM short_url_tags st JOIN tags t ON t.id = st.tag_id
                 WHERE st.short_url_id = vi.short_url_id AND t.name = @scopeTagName)"""
        | VisitScope.Domain(DomainId id) ->
            p.Add("scopeDomainId", id)

            $"""{Sql.isValidVisit "vi"} AND EXISTS (
                 SELECT 1 FROM short_urls su WHERE su.id = vi.short_url_id AND su.domain_id = @scopeDomainId)"""
        | VisitScope.Orphan -> Sql.isOrphanVisit "vi"

    let private rangeWhere (startDate: DateTime option) (endDate: DateTime option) (p: DynamicParameters) : string =
        [ match startDate with
          | Some d ->
              p.Add("rangeStart", d)
              yield "vi.visited_at >= @rangeStart"
          | None -> ()
          match endDate with
          | Some d ->
              p.Add("rangeEnd", d)
              yield "vi.visited_at <= @rangeEnd"
          | None -> () ]
        |> function
            | [] -> ""
            | parts -> " AND " + String.Join(" AND ", parts)

    /// Daily visit counts within a range for the given scope.
    let visitsPerDay
        (db: Db)
        (scope: VisitScope)
        (startDate: DateTime option)
        (endDate: DateTime option)
        : Task<(string * int64) list> =
        task {
            use conn = db.CreateConnection()
            let p = DynamicParameters()
            let whereClause = scopeWhere scope p + rangeWhere startDate endDate p
            let dayExpr = db.DayExpr "vi.visited_at"

            let! rows =
                conn.QueryAsync<DayCountRow>(
                    $"""SELECT {dayExpr} AS day, COUNT(*) AS count
                        FROM visits vi WHERE {whereClause}
                        GROUP BY {dayExpr} ORDER BY day""",
                    p)

            return rows |> Seq.map (fun r -> r.Day, r.Count) |> List.ofSeq
        }

    /// Visit counts grouped by an attribute (country, city, browser, os, referer, device).
    let breakdown
        (db: Db)
        (scope: VisitScope)
        (column: string)
        (startDate: DateTime option)
        (endDate: DateTime option)
        (limit: int)
        : Task<(string option * int64) list> =
        task {
            let column =
                // Whitelist: this ends up in SQL directly.
                match column with
                | "country_name" | "country_code" | "city" | "browser" | "os" | "referer" | "device" -> column
                | other -> invalidArg (nameof column) $"Unsupported breakdown column: {other}"

            use conn = db.CreateConnection()
            let p = DynamicParameters()
            let whereClause = scopeWhere scope p + rangeWhere startDate endDate p
            p.Add("limit", limit)

            let! rows =
                conn.QueryAsync<CountRow>(
                    $"""SELECT vi.{column} AS label, COUNT(*) AS count
                        FROM visits vi WHERE {whereClause}
                        GROUP BY vi.{column} ORDER BY count DESC
                        LIMIT @limit""",
                    p)

            return rows |> Seq.map (fun r -> r.Label, r.Count) |> List.ofSeq
        }

    let visitCount (db: Db) (scope: VisitScope) (startDate: DateTime option) (endDate: DateTime option) : Task<int64> =
        task {
            use conn = db.CreateConnection()
            let p = DynamicParameters()
            let whereClause = scopeWhere scope p + rangeWhere startDate endDate p
            return! conn.ExecuteScalarAsync<int64>($"SELECT COUNT(*) FROM visits vi WHERE {whereClause}", p)
        }

    let overview (db: Db) : Task<OverviewRow> =
        task {
            use conn = db.CreateConnection()

            return!
                conn.QuerySingleAsync<OverviewRow>(
                    $"""SELECT
                          (SELECT COUNT(*) FROM short_urls) AS short_url_count,
                          (SELECT COUNT(*) FROM visits vi WHERE {Sql.isValidVisit "vi"}) AS visit_count,
                          (SELECT COUNT(*) FROM visits vi WHERE {Sql.isOrphanVisit "vi"}) AS orphan_visit_count,
                          (SELECT COUNT(*) FROM tags) AS tag_count,
                          (SELECT COUNT(*) FROM visits vi WHERE {Sql.isValidVisit "vi"} AND vi.is_bot = {db.BoolLiteral true}) AS bot_visit_count""")
        }
