namespace Shortlink.Data

open System
open System.Threading.Tasks
open Dapper
open Shortlink.Core

[<CLIMutable>]
type DomainStatsRow =
    { Id: int64
      Authority: string
      BaseUrlRedirect: string option
      Regular404Redirect: string option
      InvalidShortUrlRedirect: string option
      IsDefault: bool
      CreatedAt: DateTime
      ShortUrlCount: int64
      VisitCount: int64 }

module DomainRepo =

    let private selectCols =
        "id, authority, base_url_redirect, regular_404_redirect, invalid_short_url_redirect, is_default, created_at"

    /// Make sure the configured default domain exists and is flagged default.
    let ensureDefault (db: Db) (authority: DomainAuthority) : Task<DomainRow> =
        task {
            use conn = db.CreateConnection()
            let! _ =
                conn.ExecuteAsync(
                    """INSERT INTO domains (authority, is_default, created_at)
                       VALUES (@authority, @isDefault, @now)
                       ON CONFLICT (authority) DO NOTHING""",
                    {| authority = authority.Value; isDefault = true; now = DateTime.UtcNow |})
            let! _ =
                conn.ExecuteAsync(
                    "UPDATE domains SET is_default = (authority = @authority)",
                    {| authority = authority.Value |})
            let! row =
                conn.QuerySingleAsync<DomainRow>(
                    $"SELECT {selectCols} FROM domains WHERE authority = @authority",
                    {| authority = authority.Value |})
            return row
        }

    let tryGetByAuthority (db: Db) (authority: string) : Task<DomainRow option> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<DomainRow>(
                    $"SELECT {selectCols} FROM domains WHERE authority = @authority",
                    {| authority = authority |})
            return Seq.tryHead rows
        }

    let tryGetById (db: Db) (DomainId id) : Task<DomainRow option> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<DomainRow>(
                    $"SELECT {selectCols} FROM domains WHERE id = @id", {| id = id |})
            return Seq.tryHead rows
        }

    let getDefault (db: Db) : Task<DomainRow> =
        task {
            use conn = db.CreateConnection()
            return! conn.QuerySingleAsync<DomainRow>(
                $"SELECT {selectCols} FROM domains WHERE is_default = @t LIMIT 1", {| t = true |})
        }

    let list (db: Db) : Task<DomainRow list> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<DomainRow>(
                    $"SELECT {selectCols} FROM domains ORDER BY is_default DESC, authority")
            return List.ofSeq rows
        }

    let listWithStats (db: Db) : Task<DomainStatsRow list> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<DomainStatsRow>(
                    """SELECT d.id, d.authority, d.base_url_redirect, d.regular_404_redirect,
                              d.invalid_short_url_redirect, d.is_default, d.created_at,
                              (SELECT COUNT(*) FROM short_urls su WHERE su.domain_id = d.id) AS short_url_count,
                              (SELECT COUNT(*) FROM visits v
                                 JOIN short_urls su ON su.id = v.short_url_id
                                WHERE su.domain_id = d.id) AS visit_count
                       FROM domains d
                       ORDER BY d.is_default DESC, d.authority""")
            return List.ofSeq rows
        }

    /// Create a non-default domain. Returns None if the authority already exists.
    let create (db: Db) (authority: DomainAuthority) : Task<DomainRow option> =
        task {
            use conn = db.CreateConnection()
            let! affected =
                conn.ExecuteAsync(
                    """INSERT INTO domains (authority, is_default, created_at)
                       VALUES (@authority, @f, @now)
                       ON CONFLICT (authority) DO NOTHING""",
                    {| authority = authority.Value; f = false; now = DateTime.UtcNow |})
            if affected = 0 then
                return None
            else
                let! rows =
                    conn.QueryAsync<DomainRow>(
                        $"SELECT {selectCols} FROM domains WHERE authority = @authority",
                        {| authority = authority.Value |})
                return Seq.tryHead rows
        }

    let updateRedirects
        (db: Db)
        (DomainId id)
        (baseUrlRedirect: string option)
        (regular404Redirect: string option)
        (invalidShortUrlRedirect: string option)
        : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected =
                conn.ExecuteAsync(
                    """UPDATE domains
                       SET base_url_redirect = @b, regular_404_redirect = @r, invalid_short_url_redirect = @i
                       WHERE id = @id""",
                    {| id = id; b = baseUrlRedirect; r = regular404Redirect; i = invalidShortUrlRedirect |})
            return affected > 0
        }

    /// Delete a domain (cascades to its short URLs). The default domain cannot be deleted.
    let delete (db: Db) (DomainId id) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected =
                conn.ExecuteAsync(
                    "DELETE FROM domains WHERE id = @id AND is_default = @f",
                    {| id = id; f = false |})
            return affected > 0
        }
