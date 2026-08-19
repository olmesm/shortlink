namespace Shortlink.Data

open System
open System.Threading.Tasks
open Dapper

/// Which set of visits a stats query aggregates over.
type VisitScope =
    | GlobalVisits
    | ShortUrlVisits of shortUrlId: int64
    | TagVisits of tagName: string
    | DomainVisits of domainId: int64
    | OrphanVisits

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
        | GlobalVisits -> "vi.visit_type = 'valid'"
        | ShortUrlVisits id ->
            p.Add("scopeShortUrlId", id)
            "vi.visit_type = 'valid' AND vi.short_url_id = @scopeShortUrlId"
        | TagVisits name ->
            p.Add("scopeTagName", name)
            """vi.visit_type = 'valid' AND EXISTS (
                 SELECT 1 FROM short_url_tags st JOIN tags t ON t.id = st.tag_id
                 WHERE st.short_url_id = vi.short_url_id AND t.name = @scopeTagName)"""
        | DomainVisits id ->
            p.Add("scopeDomainId", id)
            """vi.visit_type = 'valid' AND EXISTS (
                 SELECT 1 FROM short_urls su WHERE su.id = vi.short_url_id AND su.domain_id = @scopeDomainId)"""
        | OrphanVisits -> "vi.visit_type <> 'valid'"

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
            let t = match db.Dialect with Sqlite -> "1" | Postgres -> "TRUE"
            return! conn.QuerySingleAsync<OverviewRow>(
                $"""SELECT
                      (SELECT COUNT(*) FROM short_urls) AS short_url_count,
                      (SELECT COUNT(*) FROM visits WHERE visit_type = 'valid') AS visit_count,
                      (SELECT COUNT(*) FROM visits WHERE visit_type <> 'valid') AS orphan_visit_count,
                      (SELECT COUNT(*) FROM tags) AS tag_count,
                      (SELECT COUNT(*) FROM visits WHERE visit_type = 'valid' AND is_bot = {t}) AS bot_visit_count""")
        }
