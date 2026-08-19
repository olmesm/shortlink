namespace Shortlink.Data

open System
open System.Data.Common
open System.Threading.Tasks
open Dapper
open Microsoft.Data.Sqlite
open Npgsql
open Shortlink.Core

/// Write model for a new short URL. Fields that carry domain meaning are the
/// validated domain types; the repository unwraps them at the SQL boundary.
type NewShortUrl =
    { ShortCode: ShortCode
      DomainId: DomainId
      LongUrl: LongUrl
      Title: string option
      RedirectStatus: RedirectStatus
      ForwardQuery: bool
      Crawlable: bool
      Lifetime: Lifetime
      AuthorUserId: UserId option
      AuthorApiKeyId: ApiKeyId option }

/// Final values for every editable field of a short URL.
type ShortUrlUpdate =
    { LongUrl: LongUrl
      Title: string option
      TitleWasAutoResolved: bool
      RedirectStatus: RedirectStatus
      ForwardQuery: bool
      Crawlable: bool
      Lifetime: Lifetime }

[<RequireQualifiedAccess>]
type ShortUrlOrder =
    | DateCreated
    | ShortCode
    | LongUrl
    | Title
    | Visits

type ShortUrlFilters =
    { SearchTerm: string option
      Tags: string list
      TagsMatchAll: bool
      StartDate: DateTime option
      EndDate: DateTime option
      DomainId: DomainId option
      AuthorApiKeyId: ApiKeyId option
      ExcludeMaxVisitsReached: bool
      ExcludePastValidUntil: bool
      OrderBy: ShortUrlOrder
      Descending: bool
      Page: int
      ItemsPerPage: int }

[<RequireQualifiedAccess>]
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
          OrderBy = ShortUrlOrder.DateCreated
          Descending = true
          Page = 1
          ItemsPerPage = Paging.defaultPageSize }

[<RequireQualifiedAccess>]
type InsertShortUrlError = DuplicateShortCode

module ShortUrlRepo =

    let private validVisit = Sql.isValidVisit "v"

    let private visitCountExpr =
        $"(SELECT COUNT(*) FROM visits v WHERE v.short_url_id = su.id AND {validVisit})"

    let private detailSelect (db: Db) =
        let botCount =
            $"(SELECT COUNT(*) FROM visits v WHERE v.short_url_id = su.id AND {validVisit} AND v.is_bot = {db.BoolLiteral true})"

        $"""SELECT su.id, su.short_code, su.domain_id, d.authority, su.long_url, su.title,
                  su.title_was_auto_resolved, su.redirect_status, su.forward_query, su.crawlable,
                  su.max_visits, su.valid_since, su.valid_until, su.author_user_id, su.author_api_key_id,
                  su.created_at,
                  {visitCountExpr} AS visit_count,
                  {botCount} AS bot_visit_count
           FROM short_urls su
           JOIN domains d ON d.id = su.domain_id"""

    let private isDuplicateKey (ex: exn) =
        match ex with
        | :? SqliteException as e -> e.SqliteErrorCode = 19
        | :? PostgresException as e -> e.SqlState = "23505"
        | :? DbException -> false
        | _ -> false

    let private insertParams (nu: NewShortUrl) =
        {| ShortCode = nu.ShortCode.Value
           DomainId = nu.DomainId.Value
           LongUrl = nu.LongUrl.Value
           Title = nu.Title
           f = false
           RedirectStatus = nu.RedirectStatus.Code
           ForwardQuery = nu.ForwardQuery
           Crawlable = nu.Crawlable
           MaxVisits = nu.Lifetime.MaxVisits
           ValidSince = nu.Lifetime.ValidSince
           ValidUntil = nu.Lifetime.ValidUntil
           AuthorUserId = nu.AuthorUserId |> Option.map (fun id -> id.Value)
           AuthorApiKeyId = nu.AuthorApiKeyId |> Option.map (fun id -> id.Value)
           now = DateTime.UtcNow |}

    [<Literal>]
    let private insertSql =
        """INSERT INTO short_urls
             (short_code, domain_id, long_url, title, title_was_auto_resolved,
              redirect_status, forward_query, crawlable, max_visits,
              valid_since, valid_until, author_user_id, author_api_key_id, created_at)
           VALUES (@ShortCode, @DomainId, @LongUrl, @Title, @f, @RedirectStatus,
                   @ForwardQuery, @Crawlable, @MaxVisits, @ValidSince, @ValidUntil,
                   @AuthorUserId, @AuthorApiKeyId, @now)
           RETURNING id"""

    /// Atomically insert a short URL together with its tag links. The tag
    /// links belong to the short URL's consistency boundary, which is why
    /// this repository owns the whole write: a short URL is never observable
    /// half-created.
    let create (db: Db) (nu: NewShortUrl) (tags: TagName list) : Task<Result<ShortUrlId, InsertShortUrlError>> =
        task {
            try
                let! id =
                    Db.withTransaction db (fun conn tx ->
                        task {
                            let! id =
                                conn.ExecuteScalarAsync<int64>(insertSql, insertParams nu, transaction = tx)

                            for tag in tags do
                                let! _ =
                                    conn.ExecuteAsync(
                                        "INSERT INTO tags (name) VALUES (@name) ON CONFLICT (name) DO NOTHING",
                                        {| name = tag.Value |},
                                        transaction = tx)

                                let! _ =
                                    conn.ExecuteAsync(
                                        """INSERT INTO short_url_tags (short_url_id, tag_id)
                                           SELECT @id, id FROM tags WHERE name = @name
                                           ON CONFLICT (short_url_id, tag_id) DO NOTHING""",
                                        {| id = id; name = tag.Value |},
                                        transaction = tx)

                                ()

                            return id
                        })

                return Ok(ShortUrlId id)
            with ex when isDuplicateKey ex ->
                return Error InsertShortUrlError.DuplicateShortCode
        }

    /// Look up by a *candidate* code from the URL path — untrusted input, so a
    /// plain string is the honest parameter type here.
    let tryGetByCode (db: Db) (DomainId domainId) (code: string) : Task<ShortUrlRow option> =
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

    let tryGetDetail (db: Db) (DomainId domainId) (code: string) : Task<ShortUrlDetail option> =
        task {
            use conn = db.CreateConnection()

            let! rows =
                conn.QueryAsync<ShortUrlDetail>(
                    detailSelect db + " WHERE su.domain_id = @domainId AND su.short_code = @code",
                    {| domainId = domainId; code = code |})

            return Seq.tryHead rows
        }

    let tryFindByLongUrl (db: Db) (DomainId domainId) (longUrl: LongUrl) : Task<ShortUrlDetail option> =
        task {
            use conn = db.CreateConnection()

            let! rows =
                conn.QueryAsync<ShortUrlDetail>(
                    detailSelect db
                    + " WHERE su.domain_id = @domainId AND su.long_url = @longUrl ORDER BY su.id LIMIT 1",
                    {| domainId = domainId; longUrl = longUrl.Value |})

            return Seq.tryHead rows
        }

    let tryGetDetailById (db: Db) (ShortUrlId id) : Task<ShortUrlDetail option> =
        task {
            use conn = db.CreateConnection()
            let! rows = conn.QueryAsync<ShortUrlDetail>(detailSelect db + " WHERE su.id = @id", {| id = id |})
            return Seq.tryHead rows
        }

    let update (db: Db) (ShortUrlId id) (u: ShortUrlUpdate) : Task<bool> =
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
                       LongUrl = u.LongUrl.Value
                       Title = u.Title
                       TitleWasAutoResolved = u.TitleWasAutoResolved
                       RedirectStatus = u.RedirectStatus.Code
                       ForwardQuery = u.ForwardQuery
                       Crawlable = u.Crawlable
                       MaxVisits = u.Lifetime.MaxVisits
                       ValidSince = u.Lifetime.ValidSince
                       ValidUntil = u.Lifetime.ValidUntil |})

            return affected > 0
        }

    /// Replace the tag set of an existing short URL, atomically.
    let setTags (db: Db) (ShortUrlId id) (tags: TagName list) : Task<unit> =
        Db.withTransaction db (fun conn tx ->
            task {
                let! _ =
                    conn.ExecuteAsync(
                        "DELETE FROM short_url_tags WHERE short_url_id = @id", {| id = id |}, transaction = tx)

                for tag in tags do
                    let! _ =
                        conn.ExecuteAsync(
                            "INSERT INTO tags (name) VALUES (@name) ON CONFLICT (name) DO NOTHING",
                            {| name = tag.Value |},
                            transaction = tx)

                    let! _ =
                        conn.ExecuteAsync(
                            """INSERT INTO short_url_tags (short_url_id, tag_id)
                               SELECT @id, id FROM tags WHERE name = @name
                               ON CONFLICT (short_url_id, tag_id) DO NOTHING""",
                            {| id = id; name = tag.Value |},
                            transaction = tx)

                    ()

                return ()
            })

    let setResolvedTitle (db: Db) (ShortUrlId id) (title: string) : Task<unit> =
        task {
            use conn = db.CreateConnection()

            let! _ =
                conn.ExecuteAsync(
                    """UPDATE short_urls SET title = @title, title_was_auto_resolved = @t
                       WHERE id = @id AND title IS NULL""",
                    {| id = id; title = title; t = true |})

            return ()
        }

    let delete (db: Db) (ShortUrlId id) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected = conn.ExecuteAsync("DELETE FROM short_urls WHERE id = @id", {| id = id |})
            return affected > 0
        }

    /// Short URLs that still need automatic title resolution.
    let listMissingTitles (db: Db) (limit: int) : Task<(ShortUrlId * string) list> =
        task {
            use conn = db.CreateConnection()

            let! rows =
                conn.QueryAsync<IdUrlRow>(
                    """SELECT id, long_url FROM short_urls
                       WHERE title IS NULL ORDER BY id DESC LIMIT @limit""",
                    {| limit = limit |})

            return rows |> Seq.map (fun r -> ShortUrlId r.Id, r.LongUrl) |> List.ofSeq
        }

    /// All crawlable short URLs, for robots.txt generation.
    let listCrawlable (db: Db) : Task<string list> =
        task {
            use conn = db.CreateConnection()

            let! rows =
                conn.QueryAsync<string>(
                    $"SELECT short_code FROM short_urls WHERE crawlable = {db.BoolLiteral true} ORDER BY short_code")

            return List.ofSeq rows
        }

    let countValidVisits (db: Db) (ShortUrlId id) : Task<int64> =
        task {
            use conn = db.CreateConnection()

            return!
                conn.ExecuteScalarAsync<int64>(
                    $"SELECT COUNT(*) FROM visits v WHERE v.short_url_id = @id AND {validVisit}",
                    {| id = id |})
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
            | Some(DomainId id) ->
                p.Add("domainId", id)
                conditions.Add("su.domain_id = @domainId")
            | None -> ()

            match filters.AuthorApiKeyId with
            | Some(ApiKeyId id) ->
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
                | ShortUrlOrder.DateCreated -> "su.created_at"
                | ShortUrlOrder.ShortCode -> "su.short_code"
                | ShortUrlOrder.LongUrl -> "su.long_url"
                | ShortUrlOrder.Title -> "su.title"
                | ShortUrlOrder.Visits -> "visit_count"

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
        | "query-param" -> row.MatchKey |> Option.map (fun key -> QueryParamIs(key, row.MatchValue))
        | "ip-address" -> Some(IpInRange row.MatchValue)
        | _ -> None

    let getRules (db: Db) (ShortUrlId shortUrlId) : Task<RedirectRule list> =
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

    let private conditionToRow (cond: RuleCondition) =
        match cond with
        | DeviceIs d -> {| CondType = "device"; MatchKey = None; MatchValue = d.Slug |}
        | LanguageIs l -> {| CondType = "language"; MatchKey = None; MatchValue = l |}
        | QueryParamIs(k, v) -> {| CondType = "query-param"; MatchKey = Some k; MatchValue = v |}
        | IpInRange cidr -> {| CondType = "ip-address"; MatchKey = None; MatchValue = cidr |}

    /// Replace all redirect rules of a short URL, atomically.
    let setRules (db: Db) (ShortUrlId shortUrlId) (rules: RedirectRule list) : Task<unit> =
        Db.withTransaction db (fun conn tx ->
            task {
                let! _ =
                    conn.ExecuteAsync(
                        "DELETE FROM redirect_rules WHERE short_url_id = @id",
                        {| id = shortUrlId |},
                        transaction = tx)

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
                                {| rid = ruleId
                                   condType = c.CondType
                                   matchKey = c.MatchKey
                                   matchValue = c.MatchValue |},
                                transaction = tx)

                        ()

                return ()
            })
