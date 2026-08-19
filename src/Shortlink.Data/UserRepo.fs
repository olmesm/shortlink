namespace Shortlink.Data

open System
open System.Threading.Tasks
open Dapper

module UserRepo =

    let private selectCols = "id, username, password_hash, role, created_at"

    /// Create a user. Returns None if the username is taken.
    let insert (db: Db) (username: string) (passwordHash: string) (role: string) : Task<UserRow option> =
        task {
            use conn = db.CreateConnection()
            let! affected =
                conn.ExecuteAsync(
                    """INSERT INTO users (username, password_hash, role, created_at)
                       VALUES (@username, @passwordHash, @role, @now)
                       ON CONFLICT (username) DO NOTHING""",
                    {| username = username; passwordHash = passwordHash; role = role; now = DateTime.UtcNow |})
            if affected = 0 then
                return None
            else
                let! rows =
                    conn.QueryAsync<UserRow>(
                        $"SELECT {selectCols} FROM users WHERE username = @username",
                        {| username = username |})
                return Seq.tryHead rows
        }

    let tryFindByUsername (db: Db) (username: string) : Task<UserRow option> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<UserRow>(
                    $"SELECT {selectCols} FROM users WHERE username = @username",
                    {| username = username |})
            return Seq.tryHead rows
        }

    let tryFindById (db: Db) (id: int64) : Task<UserRow option> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<UserRow>($"SELECT {selectCols} FROM users WHERE id = @id", {| id = id |})
            return Seq.tryHead rows
        }

    let list (db: Db) : Task<UserRow list> =
        task {
            use conn = db.CreateConnection()
            let! rows = conn.QueryAsync<UserRow>($"SELECT {selectCols} FROM users ORDER BY username")
            return List.ofSeq rows
        }

    let updatePassword (db: Db) (id: int64) (passwordHash: string) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected =
                conn.ExecuteAsync(
                    "UPDATE users SET password_hash = @hash WHERE id = @id",
                    {| id = id; hash = passwordHash |})
            return affected > 0
        }

    let updateRole (db: Db) (id: int64) (role: string) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected =
                conn.ExecuteAsync("UPDATE users SET role = @role WHERE id = @id", {| id = id; role = role |})
            return affected > 0
        }

    let delete (db: Db) (id: int64) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected = conn.ExecuteAsync("DELETE FROM users WHERE id = @id", {| id = id |})
            return affected > 0
        }

    let count (db: Db) : Task<int64> =
        task {
            use conn = db.CreateConnection()
            return! conn.ExecuteScalarAsync<int64>("SELECT COUNT(*) FROM users")
        }

    let countAdmins (db: Db) : Task<int64> =
        task {
            use conn = db.CreateConnection()
            return! conn.ExecuteScalarAsync<int64>("SELECT COUNT(*) FROM users WHERE role = 'admin'")
        }
