namespace Shortlink.Data

open System
open System.Threading.Tasks
open Dapper

module WebhookRepo =

    let private selectCols = "id, name, url, secret, events, enabled, created_at"

    let insert (db: Db) (name: string) (url: string) (secret: string) (events: string list) : Task<WebhookRow> =
        task {
            use conn = db.CreateConnection()
            let! id =
                conn.ExecuteScalarAsync<int64>(
                    """INSERT INTO webhooks (name, url, secret, events, enabled, created_at)
                       VALUES (@name, @url, @secret, @events, @t, @now)
                       RETURNING id""",
                    {| name = name
                       url = url
                       secret = secret
                       events = String.Join(",", events)
                       t = true
                       now = DateTime.UtcNow |})
            let! row =
                conn.QuerySingleAsync<WebhookRow>(
                    $"SELECT {selectCols} FROM webhooks WHERE id = @id", {| id = id |})
            return row
        }

    let list (db: Db) : Task<WebhookRow list> =
        task {
            use conn = db.CreateConnection()
            let! rows = conn.QueryAsync<WebhookRow>($"SELECT {selectCols} FROM webhooks ORDER BY name")
            return List.ofSeq rows
        }

    /// Enabled webhooks subscribed to a given event.
    let listForEvent (db: Db) (eventSlug: string) : Task<WebhookRow list> =
        task {
            use conn = db.CreateConnection()
            let t = match db.Dialect with Sqlite -> "1" | Postgres -> "TRUE"
            let! rows =
                conn.QueryAsync<WebhookRow>(
                    $"SELECT {selectCols} FROM webhooks WHERE enabled = {t}")
            return
                rows
                |> Seq.filter (fun w -> w.Events.Split(',') |> Array.exists (fun e -> e.Trim() = eventSlug))
                |> List.ofSeq
        }

    let setEnabled (db: Db) (id: int64) (enabled: bool) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected =
                conn.ExecuteAsync(
                    "UPDATE webhooks SET enabled = @enabled WHERE id = @id",
                    {| id = id; enabled = enabled |})
            return affected > 0
        }

    let delete (db: Db) (id: int64) : Task<bool> =
        task {
            use conn = db.CreateConnection()
            let! affected = conn.ExecuteAsync("DELETE FROM webhooks WHERE id = @id", {| id = id |})
            return affected > 0
        }

    // ---- Delivery queue ----

    let enqueueDelivery (db: Db) (webhookId: int64) (eventSlug: string) (payload: string) : Task<unit> =
        task {
            use conn = db.CreateConnection()
            let! _ =
                conn.ExecuteAsync(
                    """INSERT INTO webhook_deliveries (webhook_id, event, payload, attempts, next_attempt_at, status, created_at)
                       VALUES (@webhookId, @event, @payload, 0, @now, 'pending', @now)""",
                    {| webhookId = webhookId; event = eventSlug; payload = payload; now = DateTime.UtcNow |})
            return ()
        }

    /// Deliveries due for an attempt, joined with their webhook config.
    let dueDeliveries (db: Db) (limit: int) : Task<(WebhookDeliveryRow * WebhookRow) list> =
        task {
            use conn = db.CreateConnection()
            let! rows =
                conn.QueryAsync<WebhookDeliveryRow, WebhookRow, WebhookDeliveryRow * WebhookRow>(
                    $"""SELECT wd.id, wd.webhook_id, wd.event, wd.payload, wd.attempts, wd.next_attempt_at,
                              wd.status, wd.last_error, wd.created_at,
                              w.id, w.name, w.url, w.secret, w.events, w.enabled, w.created_at
                       FROM webhook_deliveries wd
                       JOIN webhooks w ON w.id = wd.webhook_id
                       WHERE wd.status = 'pending' AND wd.next_attempt_at <= @now
                       ORDER BY wd.next_attempt_at LIMIT @limit""",
                    (fun d w -> d, w),
                    {| now = DateTime.UtcNow; limit = limit |},
                    splitOn = "id")
            return List.ofSeq rows
        }

    let markDelivered (db: Db) (deliveryId: int64) : Task<unit> =
        task {
            use conn = db.CreateConnection()
            let! _ =
                conn.ExecuteAsync(
                    "UPDATE webhook_deliveries SET status = 'delivered' WHERE id = @id",
                    {| id = deliveryId |})
            return ()
        }

    /// Record a failed attempt; retries with exponential backoff, giving up after maxAttempts.
    let markFailedAttempt (db: Db) (deliveryId: int64) (attempts: int) (maxAttempts: int) (error: string) : Task<unit> =
        task {
            use conn = db.CreateConnection()
            let newAttempts = attempts + 1
            if newAttempts >= maxAttempts then
                let! _ =
                    conn.ExecuteAsync(
                        """UPDATE webhook_deliveries SET status = 'failed', attempts = @attempts, last_error = @error
                           WHERE id = @id""",
                        {| id = deliveryId; attempts = newAttempts; error = error |})
                ()
            else
                let delay = TimeSpan.FromSeconds(float (pown 2 newAttempts) * 15.0)
                let! _ =
                    conn.ExecuteAsync(
                        """UPDATE webhook_deliveries SET attempts = @attempts, last_error = @error, next_attempt_at = @next
                           WHERE id = @id""",
                        {| id = deliveryId
                           attempts = newAttempts
                           error = error
                           next = DateTime.UtcNow.Add delay |})
                ()
            return ()
        }
