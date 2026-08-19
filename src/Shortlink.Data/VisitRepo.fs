namespace Shortlink.Data

open System
open System.Threading.Tasks
open Dapper
open Shortlink.Core

type NewVisit =
    { ShortUrlId: ShortUrlId option
      VisitType: VisitType
      VisitedAt: DateTime
      Referer: string option
      UserAgent: string option
      Browser: string option
      Os: string option
      Device: Device
      IsBot: bool
      RemoteIp: string option
      VisitedUrl: string option }

/// Geolocation result for one visit. None everywhere = lookup found nothing.
type GeoInfo =
    { CountryCode: string option
      CountryName: string option
      City: string option
      Latitude: float option
      Longitude: float option }

type VisitFilters =
    { StartDate: DateTime option
      EndDate: DateTime option
      ExcludeBots: bool
      Page: int
      ItemsPerPage: int }

[<RequireQualifiedAccess>]
module VisitFilters =
    let empty =
        { StartDate = None
          EndDate = None
          ExcludeBots = false
          Page = 1
          ItemsPerPage = Paging.defaultPageSize }

module VisitRepo =

    let private selectCols =
        """id, short_url_id, visit_type, visited_at, referer, user_agent, browser, os, device,
           is_bot, remote_ip, country_code, country_name, city, latitude, longitude, visited_url, geo_resolved"""

    let insert (db: Db) (v: NewVisit) : Task<VisitId> =
        task {
            use conn = db.CreateConnection()

            let! id =
                conn.ExecuteScalarAsync<int64>(
                    """INSERT INTO visits
                         (short_url_id, visit_type, visited_at, referer, user_agent, browser, os, device,
                          is_bot, remote_ip, visited_url, geo_resolved)
                       VALUES (@ShortUrlId, @VisitType, @VisitedAt, @Referer, @UserAgent, @Browser, @Os,
                               @Device, @IsBot, @RemoteIp, @VisitedUrl, @geoResolved)
                       RETURNING id""",
                    {| ShortUrlId = v.ShortUrlId |> Option.map (fun id -> id.Value)
                       VisitType = v.VisitType.Slug
                       VisitedAt = v.VisitedAt
                       Referer = v.Referer
                       UserAgent = v.UserAgent
                       Browser = v.Browser
                       Os = v.Os
                       Device = v.Device.Slug
                       IsBot = v.IsBot
                       RemoteIp = v.RemoteIp
                       VisitedUrl = v.VisitedUrl
                       geoResolved = false |})

            return VisitId id
        }

    let setGeo (db: Db) (VisitId visitId) (geo: GeoInfo) : Task<unit> =
        task {
            use conn = db.CreateConnection()

            let! _ =
                conn.ExecuteAsync(
                    """UPDATE visits SET country_code = @cc, country_name = @cn, city = @city,
                                         latitude = @lat, longitude = @lon, geo_resolved = @t
                       WHERE id = @id""",
                    {| id = visitId
                       cc = geo.CountryCode
                       cn = geo.CountryName
                       city = geo.City
                       lat = geo.Latitude
                       lon = geo.Longitude
                       t = true |})

            return ()
        }

    let markGeoResolved (db: Db) (VisitId visitId) : Task<unit> =
        task {
            use conn = db.CreateConnection()
            let! _ = conn.ExecuteAsync("UPDATE visits SET geo_resolved = @t WHERE id = @id", {| id = visitId; t = true |})
            return ()
        }

    /// Visits still awaiting geolocation (with a usable IP).
    let listPendingGeo (db: Db) (limit: int) : Task<(VisitId * string) list> =
        task {
            use conn = db.CreateConnection()

            let! rows =
                conn.QueryAsync<IdIpRow>(
                    $"""SELECT id, remote_ip FROM visits
                        WHERE geo_resolved = {db.BoolLiteral false} AND remote_ip IS NOT NULL
                        ORDER BY id LIMIT @limit""",
                    {| limit = limit |})

            return rows |> Seq.map (fun r -> VisitId r.Id, r.RemoteIp) |> List.ofSeq
        }

    let private buildFilterSql (db: Db) (filters: VisitFilters) (p: DynamicParameters) =
        let conditions = ResizeArray<string>()

        match filters.StartDate with
        | Some d ->
            p.Add("startDate", d)
            conditions.Add("vi.visited_at >= @startDate")
        | None -> ()

        match filters.EndDate with
        | Some d ->
            p.Add("endDate", d)
            conditions.Add("vi.visited_at <= @endDate")
        | None -> ()

        if filters.ExcludeBots then
            conditions.Add($"vi.is_bot = {db.BoolLiteral false}")

        conditions

    let private pageQuery (db: Db) (baseWhere: string) (filters: VisitFilters) (p: DynamicParameters) : Task<Paging.Page<VisitRow>> =
        task {
            use conn = db.CreateConnection()
            let page, size = Paging.normalize filters.Page filters.ItemsPerPage
            let extra = buildFilterSql db filters p

            let whereClause =
                if extra.Count = 0 then baseWhere
                else baseWhere + " AND " + String.Join(" AND ", extra)

            let! total = conn.ExecuteScalarAsync<int64>($"SELECT COUNT(*) FROM visits vi WHERE {whereClause}", p)
            p.Add("limit", size)
            p.Add("offset", Paging.offset page size)

            let! rows =
                conn.QueryAsync<VisitRow>(
                    $"""SELECT {selectCols} FROM visits vi WHERE {whereClause}
                        ORDER BY vi.visited_at DESC, vi.id DESC
                        LIMIT @limit OFFSET @offset""",
                    p)

            return
                { Paging.Items = List.ofSeq rows
                  Paging.CurrentPage = page
                  Paging.ItemsPerPage = size
                  Paging.TotalItems = total }
        }

    let listForShortUrl (db: Db) (ShortUrlId shortUrlId) (filters: VisitFilters) : Task<Paging.Page<VisitRow>> =
        let p = DynamicParameters()
        p.Add("shortUrlId", shortUrlId)
        pageQuery db $"""vi.short_url_id = @shortUrlId AND {Sql.isValidVisit "vi"}""" filters p

    /// All non-orphan visits, optionally filtered.
    let listNonOrphan (db: Db) (filters: VisitFilters) : Task<Paging.Page<VisitRow>> =
        pageQuery db (Sql.isValidVisit "vi") filters (DynamicParameters())

    let listOrphan (db: Db) (visitType: VisitType option) (filters: VisitFilters) : Task<Paging.Page<VisitRow>> =
        let p = DynamicParameters()

        match visitType with
        | Some vt ->
            p.Add("visitType", vt.Slug)
            pageQuery db $"""vi.visit_type = @visitType AND {Sql.isOrphanVisit "vi"}""" filters p
        | None -> pageQuery db (Sql.isOrphanVisit "vi") filters p

    let listForTag (db: Db) (tagName: string) (filters: VisitFilters) : Task<Paging.Page<VisitRow>> =
        let p = DynamicParameters()
        p.Add("tagName", tagName)

        pageQuery
            db
            $"""{Sql.isValidVisit "vi"} AND EXISTS (
                 SELECT 1 FROM short_url_tags st JOIN tags t ON t.id = st.tag_id
                 WHERE st.short_url_id = vi.short_url_id AND t.name = @tagName)"""
            filters
            p

    let listForDomain (db: Db) (DomainId domainId) (filters: VisitFilters) : Task<Paging.Page<VisitRow>> =
        let p = DynamicParameters()
        p.Add("domainId", domainId)

        pageQuery
            db
            $"""{Sql.isValidVisit "vi"} AND EXISTS (
                 SELECT 1 FROM short_urls su WHERE su.id = vi.short_url_id AND su.domain_id = @domainId)"""
            filters
            p

    let deleteForShortUrl (db: Db) (ShortUrlId shortUrlId) : Task<int> =
        task {
            use conn = db.CreateConnection()
            return! conn.ExecuteAsync("DELETE FROM visits WHERE short_url_id = @id", {| id = shortUrlId |})
        }

    let deleteOrphan (db: Db) : Task<int> =
        task {
            use conn = db.CreateConnection()
            return! conn.ExecuteAsync($"""DELETE FROM visits WHERE {Sql.isOrphanVisit "visits"}""")
        }
