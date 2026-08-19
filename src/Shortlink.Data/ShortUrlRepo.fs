namespace Shortlink.Data

open System
open System.Data.Common
open System.Threading.Tasks
open Dapper
open Microsoft.Data.Sqlite
open Npgsql
open Shortlink.Core

type NewShortUrl =
    { ShortCode: string
      DomainId: int64
      LongUrl: string
      Title: string option
      RedirectStatus: int
      ForwardQuery: bool
      Crawlable: bool
      MaxVisits: int64 option
      ValidSince: DateTime option
      ValidUntil: DateTime option
      AuthorUserId: int64 option
      AuthorApiKeyId: int64 option }

/// Final values for every editable field of a short URL.
type ShortUrlUpdate =
    { LongUrl: string
      Title: string option
      TitleWasAutoResolved: bool
      RedirectStatus: int
      ForwardQuery: bool
      Crawlable: bool
      MaxVisits: int64 option
      ValidSince: DateTime option
      ValidUntil: DateTime option }

type ShortUrlOrder =
    | ByDateCreated
    | ByShortCode
    | ByLongUrl
    | ByTitle
    | ByVisits

type ShortUrlFilters =
    { SearchTerm: string option
      Tags: string list
      TagsMatchAll: bool
      StartDate: DateTime option
      EndDate: DateTime option
      DomainId: int64 option
      AuthorApiKeyId: int64 option
      ExcludeMaxVisitsReached: bool
      ExcludePastValidUntil: bool
      OrderBy: ShortUrlOrder
      Descending: bool
      Page: int
      ItemsPerPage: int }

module ShortUrlFilters =
    let empty =
        { SearchTerm = None
          Tags = []
          TagsMatchAll = false
          StartDate = None
          EndDate = None
          DomainId = None
          AuthorApiKeyId = None
          ExcludeMaxVisitsReached = false
          ExcludePastValidUntil = false
          OrderBy = ByDateCreated
          Descending = true
          Page = 1
          ItemsPerPage = Paging.defaultPageSize }

type InsertShortUrlError = | DuplicateShortCode

module ShortUrlRepo =

    let private visitCountExpr =
        "(SELECT COUNT(*) FROM visits v WHERE v.short_url_id = su.id AND v.visit_type = 'valid')"

    let private botVisitCountExpr =
        "(SELECT COUNT(*) FROM visits v WHERE v.short_url_id = su.id AND v.visit_type = 'valid' AND v.is_bot = {TRUE})"

    let private detailSelect (db: Db) =
        let t = match db.Dialect with Sqlite -> "1" | Postgres -> "TRUE"
        $"""SELECT su.id, su.short_code, su.domain_id, d.authority, su.long_url, su.title,
                  su.title_was_auto_resolved, su.redirect_status, su.forward_query, su.crawlable,
                  su.max_visits, su.valid_since, su.valid_until, su.author_user_id, su.author_api_key_id,
                  su.created_at,
                  {visitCountExpr} AS visit_count,
                  {botVisitCountExpr.Replace("{TRUE}", t)} AS bot_visit_count
           FROM short_urls su
           JOIN domains d ON d.id = su.domain_id"""

    let private isDuplicateKey (ex: exn) =
        match ex with
        | :? SqliteException as e -> e.SqliteErrorCode = 19
        | :? PostgresException as e -> e.SqlState = "23505"
        | :? DbException -> false
        | _ -> false

    let insert (db: Db) (nu: NewShortUrl) : Task<Result<int64, InsertShortUrlError>> =
        task {
            use conn = db.CreateConnection()
            try
                let! id =
                    conn.ExecuteScalarAsync<int64>(
                        """INSERT INTO short_urls
                             (short_code, domain_id, long_url, title, title_was_auto_resolved,
                              redirect_status, forward_query, crawlable, max_visits,
                              valid_since, valid_until, author_user_id, author_api_key_id, created_at)
                           VALUES (@ShortCode, @DomainId, @LongUrl, @Title, @f, @RedirectStatus,
                                   @ForwardQuery, @Crawlable, @MaxVisits, @ValidSince, @ValidUntil,
                                   @AuthorUserId, @AuthorApiKeyId, @now)
                           RETURNING id""",
                        {| ShortCode = nu.ShortCode
                           DomainId = nu.DomainId
                           LongUrl = nu.LongUrl
                           Title = nu.Title
                           f = false
                           RedirectStatus = nu.RedirectStatus
                           ForwardQuery = nu.ForwardQuery
                           Crawlable = nu.Crawlable
                           MaxVisits = nu.MaxVisits
                           ValidSince = nu.ValidSince
                           ValidUntil = nu.ValidUntil
                           AuthorUserId = nu.AuthorUserId
                           AuthorApiKeyId = nu.AuthorApiKeyId
                           now = DateTime.UtcNow |})
                return Ok id
            with ex when isDuplicateKey ex ->
                return Error DuplicateShortCode
        }

    let tryGetByCode (db: Db) (domainId: int64) (code: string) : Task<ShortUrlRow option> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<ShortUrlRow>(
                    """SELECT id, short_code, domain_id, long_url, title, title_was_auto_resolved,
                              redirect_status, forward_query, crawlable, max_visits, valid_since,
                              valid_until, author_user_id, author_api_key_id, created_at
                       FROM short_urls WHERE domain_id = @domainId AND short_code = @code""",
                    {| domainId = domainId; code = code |})
            return Seq.tryHead rows
        }

    let tryGetDetail (db: Db) (domainId: int64) (code: string) : Task<ShortUrlDetail option> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<ShortUrlDetail>(
                    detailSelect db + " WHERE su.domain_id = @domainId AND su.short_code = @code",
                    {| domainId = domainId; code = code |})
            return Seq.tryHead rows
        }


    let tryFindByLongUrl (db: Db) (domainId: int64) (longUrl: string) : Task<ShortUrlDetail option> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<ShortUrlDetail>(
                    detailSelect db + " WHERE su.domain_id = @domainId AND su.long_url = @longUrl ORDER BY su.id LIMIT 1",
                    {| domainId = domainId; longUrl = longUrl |})
            return Seq.tryHead rows
        }

    let tryGetDetailById (db: Db) (id: int64) : Task<ShortUrlDetail option> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<ShortUrlDetail>(
                    detailSelect db + " WHERE su.id = @id", {| id = id |})
            return Seq.tryHead rows
        }

    let update (db: Db) (id: int64) (u: ShortUrlUpdate) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected =
                conn.ExecuteAsync(
                    """UPDATE short_urls SET
                         long_url = @LongUrl, title = @Title, title_was_auto_resolved = @TitleWasAutoResolved,
                         redirect_status = @RedirectStatus, forward_query = @ForwardQuery,
                         crawlable = @Crawlable, max_visits = @MaxVisits,
                         valid_since = @ValidSince, valid_until = @ValidUntil
                       WHERE id = @id""",
                    {| id = id
                       LongUrl = u.LongUrl
                       Title = u.Title
                       TitleWasAutoResolved = u.TitleWasAutoResolved
                       RedirectStatus = u.RedirectStatus
                       ForwardQuery = u.ForwardQuery
                       Crawlable = u.Crawlable
                       MaxVisits = u.MaxVisits
                       ValidSince = u.ValidSince
                       ValidUntil = u.ValidUntil |})
            return affected > 0
        }

    let setResolvedTitle (db: Db) (id: int64) (title: string) : Task<unit> =
        task {
            use conn = db.CreateConnection()
            let! _ =
                conn.ExecuteAsync(
                    """UPDATE short_urls SET title = @title, title_was_auto_resolved = @t
                       WHERE id = @id AND title IS NULL""",
                    {| id = id; title = title; t = true |})
            return ()
        }

    let delete (db: Db) (id: int64) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected = conn.ExecuteAsync("DELETE FROM short_urls WHERE id = @id", {| id = id |})
            return affected > 0
        }

    /// Short URLs that still need automatic title resolution.
    let listMissingTitles (db: Db) (limit: int) : Task<(int64 * string) list> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<IdUrlRow>(
                    """SELECT id, long_url FROM short_urls
                       WHERE title IS NULL ORDER BY id DESC LIMIT @limit""",
                    {| limit = limit |})
            return rows |> Seq.map (fun r -> r.Id, r.LongUrl) |> List.ofSeq
        }

    /// All crawlable short URLs, for robots.txt generation.
    let listCrawlable (db: Db) : Task<string list> =
        task {
            use conn = db.CreateConnection()
            let t = match db.Dialect with Sqlite -> "1" | Postgres -> "TRUE"
            let! rows =
                conn.QueryAsync<string>(
                    $"SELECT short_code FROM short_urls WHERE crawlable = {t} ORDER BY short_code")
            return List.ofSeq rows
        }

    let countValidVisits (db: Db) (shortUrlId: int64) : Task<int64> =
        task {
            use conn = db.CreateConnection()
            return! conn.ExecuteScalarAsync<int64>(
                "SELECT COUNT(*) FROM visits WHERE short_url_id = @id AND visit_type = 'valid'",
                {| id = shortUrlId |})
        }

    let list (db: Db) (filters: ShortUrlFilters) : Task<Paging.Page<ShortUrlDetail>> =
        task {
            use conn = db.CreateConnection()
            let page, size = Paging.normalize filters.Page filters.ItemsPerPage
            let p = DynamicParameters()
            let conditions = ResizeArray<string>()

            match filters.SearchTerm with
            | Some term when term.Trim() <> "" ->
                p.Add("search", "%" + term.Trim() + "%")
                let like col = db.ILike(col, "@search")
                conditions.Add(
                    $"""({like "su.long_url"} OR {like "coalesce(su.title, '')"} OR {like "su.short_code"} OR {like "d.authority"}
                        OR EXISTS (SELECT 1 FROM short_url_tags st JOIN tags t ON t.id = st.tag_id
                                   WHERE st.short_url_id = su.id AND {like "t.name"}))""")
            | _ -> ()

            if not filters.Tags.IsEmpty then
                p.Add("tags", List.toArray filters.Tags)
                let tagsIn = db.InList("t.name", "@tags")
                if filters.TagsMatchAll then
                    p.Add("tagCount", filters.Tags.Length)
                    conditions.Add(
                        $"""(SELECT COUNT(DISTINCT t.name) FROM short_url_tags st
                            JOIN tags t ON t.id = st.tag_id
                            WHERE st.short_url_id = su.id AND {tagsIn}) = @tagCount""")
                else
                    conditions.Add(
                        $"""EXISTS (SELECT 1 FROM short_url_tags st JOIN tags t ON t.id = st.tag_id
                                   WHERE st.short_url_id = su.id AND {tagsIn})""")

            match filters.StartDate with
            | Some d ->
                p.Add("startDate", d)
                conditions.Add("su.created_at >= @startDate")
            | None -> ()

            match filters.EndDate with
            | Some d ->
                p.Add("endDate", d)
                conditions.Add("su.created_at <= @endDate")
            | None -> ()

            match filters.DomainId with
            | Some id ->
                p.Add("domainId", id)
                conditions.Add("su.domain_id = @domainId")
            | None -> ()

            match filters.AuthorApiKeyId with
            | Some id ->
                p.Add("authorApiKeyId", id)
                conditions.Add("su.author_api_key_id = @authorApiKeyId")
            | None -> ()

            if filters.ExcludeMaxVisitsReached then
                conditions.Add($"(su.max_visits IS NULL OR {visitCountExpr} < su.max_visits)")

            if filters.ExcludePastValidUntil then
                p.Add("nowUtc", DateTime.UtcNow)
                conditions.Add("(su.valid_until IS NULL OR su.valid_until >= @nowUtc)")

            let whereClause =
                if conditions.Count = 0 then ""
                else "WHERE " + String.Join(" AND ", conditions)

            let orderCol =
                match filters.OrderBy with
                | ByDateCreated -> "su.created_at"
                | ByShortCode -> "su.short_code"
                | ByLongUrl -> "su.long_url"
                | ByTitle -> "su.title"
                | ByVisits -> "visit_count"

            let orderDir = if filters.Descending then "DESC" else "ASC"

            let! total =
                conn.ExecuteScalarAsync<int64>(
                    $"""SELECT COUNT(*) FROM short_urls su
                        JOIN domains d ON d.id = su.domain_id {whereClause}""",
                    p)

            p.Add("limit", size)
            p.Add("offset", Paging.offset page size)

            let! rows =
                conn.QueryAsync<ShortUrlDetail>(
                    $"""{detailSelect db} {whereClause}
                        ORDER BY {orderCol} {orderDir}, su.id {orderDir}
                        LIMIT @limit OFFSET @offset""",
                    p)

            return
                { Paging.Items = List.ofSeq rows
                  Paging.CurrentPage = page
                  Paging.ItemsPerPage = size
                  Paging.TotalItems = total }
        }

    // ---- Redirect rules ----

    let private parseCondition (row: RedirectConditionRow) : RuleCondition option =
        match row.CondType with
        | "device" -> Device.OfSlug row.MatchValue |> Option.map DeviceIs
        | "language" -> Some(LanguageIs row.MatchValue)
        | "query-param" ->
            match row.MatchKey with
            | Some key -> Some(QueryParamIs(key, row.MatchValue))
            | None -> None
        | "ip-address" -> Some(IpInRange row.MatchValue)
        | _ -> None

    let getRules (db: Db) (shortUrlId: int64) : Task<RedirectRule list> =
        task {
            use conn = db.CreateConnection()
            let! ruleRows =
                conn.QueryAsync<RedirectRuleRow>(
                    """SELECT id, short_url_id, priority, long_url FROM redirect_rules
                       WHERE short_url_id = @id ORDER BY priority""",
                    {| id = shortUrlId |})
            let ruleRows = List.ofSeq ruleRows
            if ruleRows.IsEmpty then
                return []
            else
                let inClause = db.InList("rule_id", "@ids")
                let! condRows =
                    conn.QueryAsync<RedirectConditionRow>(
                        $"""SELECT id, rule_id, cond_type, match_key, match_value
                           FROM redirect_conditions WHERE {inClause}""",
                        {| ids = ruleRows |> List.map (fun r -> r.Id) |> List.toArray |})
                let condsByRule =
                    condRows
                    |> Seq.groupBy (fun c -> c.RuleId)
                    |> Seq.map (fun (id, cs) -> id, cs |> Seq.choose parseCondition |> List.ofSeq)
                    |> Map.ofSeq
                return
                    ruleRows
                    |> List.map (fun r ->
                        { Priority = r.Priority
                          LongUrl = r.LongUrl
                          Conditions = condsByRule.TryFind r.Id |> Option.defaultValue [] })
        }

    let private conditionToRow (cond: RuleCondition) : {| CondType: string; MatchKey: string option; MatchValue: string |} =
        match cond with
        | DeviceIs d -> {| CondType = "device"; MatchKey = None; MatchValue = d.Slug |}
        | LanguageIs l -> {| CondType = "language"; MatchKey = None; MatchValue = l |}
        | QueryParamIs(k, v) -> {| CondType = "query-param"; MatchKey = Some k; MatchValue = v |}
        | IpInRange cidr -> {| CondType = "ip-address"; MatchKey = None; MatchValue = cidr |}

    /// Replace all redirect rules of a short URL.
    let setRules (db: Db) (shortUrlId: int64) (rules: RedirectRule list) : Task<unit> =
        task {
            use conn = db.CreateConnection()
            use tx = conn.BeginTransaction()
            let! _ =
                conn.ExecuteAsync(
                    "DELETE FROM redirect_rules WHERE short_url_id = @id",
                    {| id = shortUrlId |}, transaction = tx)
            for i, rule in List.indexed (rules |> List.sortBy (fun r -> r.Priority)) do
                let! ruleId =
                    conn.ExecuteScalarAsync<int64>(
                        """INSERT INTO redirect_rules (short_url_id, priority, long_url)
                           VALUES (@sid, @priority, @longUrl) RETURNING id""",
                        {| sid = shortUrlId; priority = i + 1; longUrl = rule.LongUrl |},
                        transaction = tx)
                for cond in rule.Conditions do
                    let c = conditionToRow cond
                    let! _ =
                        conn.ExecuteAsync(
                            """INSERT INTO redirect_conditions (rule_id, cond_type, match_key, match_value)
                               VALUES (@rid, @condType, @matchKey, @matchValue)""",
                            {| rid = ruleId; condType = c.CondType; matchKey = c.MatchKey; matchValue = c.MatchValue |},
                            transaction = tx)
                    ()
            tx.Commit()
            return ()
        }
