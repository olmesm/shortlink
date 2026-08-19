namespace Shortlink.Data

open System
open System.Threading.Tasks
open Dapper
open Shortlink.Core

module ApiKeyRepo =

    let private selectCols = "id, key_hash, name, role, domain_id, enabled, expires_at, created_at"

    let insert
        (db: Db)
        (keyHash: string)
        (name: string option)
        (role: ApiKeyRole)
        (expiresAt: DateTime option)
        : Task<ApiKeyRow> =
        task {
            use conn = db.CreateConnection()

            let domainId =
                match role with
                | ApiKeyRole.Domain(DomainId id) -> Some id
                | ApiKeyRole.Admin
                | ApiKeyRole.Author -> None

            let! id =
                conn.ExecuteScalarAsync<int64>(
                    """INSERT INTO api_keys (key_hash, name, role, domain_id, enabled, expires_at, created_at)
                       VALUES (@keyHash, @name, @role, @domainId, @t, @expiresAt, @now)
                       RETURNING id""",
                    {| keyHash = keyHash
                       name = name
                       role = role.Slug
                       domainId = domainId
                       t = true
                       expiresAt = expiresAt
                       now = DateTime.UtcNow |})

            let! row = conn.QuerySingleAsync<ApiKeyRow>($"SELECT {selectCols} FROM api_keys WHERE id = @id", {| id = id |})
            return row
        }

    let tryFindByHash (db: Db) (keyHash: string) : Task<ApiKeyRow option> =
        task {
            use conn = db.CreateConnection()

            let! rows =
                conn.QueryAsync<ApiKeyRow>(
                    $"SELECT {selectCols} FROM api_keys WHERE key_hash = @keyHash", {| keyHash = keyHash |})

            return Seq.tryHead rows
        }

    let list (db: Db) : Task<ApiKeyRow list> =
        task {
            use conn = db.CreateConnection()
            let! rows = conn.QueryAsync<ApiKeyRow>($"SELECT {selectCols} FROM api_keys ORDER BY created_at DESC")
            return List.ofSeq rows
        }

    let tryGetById (db: Db) (ApiKeyId id) : Task<ApiKeyRow option> =
        task {
            use conn = db.CreateConnection()
            let! rows = conn.QueryAsync<ApiKeyRow>($"SELECT {selectCols} FROM api_keys WHERE id = @id", {| id = id |})
            return Seq.tryHead rows
        }

    let setEnabled (db: Db) (ApiKeyId id) (enabled: bool) : Task<bool> =
        task {
            use conn = db.CreateConnection()

            let! affected =
                conn.ExecuteAsync("UPDATE api_keys SET enabled = @enabled WHERE id = @id", {| id = id; enabled = enabled |})

            return affected > 0
        }

    let delete (db: Db) (ApiKeyId id) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected = conn.ExecuteAsync("DELETE FROM api_keys WHERE id = @id", {| id = id |})
            return affected > 0
        }
