namespace Shortlink.Web

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open MaxMind.GeoIP2
open Shortlink.Data

/// Thread-safe holder around the MaxMind reader; reloadable when the
/// database file is refreshed.
type GeoIpService(cfg: AppConfig, logger: ILogger<GeoIpService>) =
    let mutable reader: DatabaseReader option = None
    let gate = obj ()

    member _.Reload() =
        lock gate (fun () ->
            let path = AppConfig.geoDbPath cfg
            if File.Exists path then
                try
                    let newReader = new DatabaseReader(path)
                    reader |> Option.iter (fun r -> r.Dispose())
                    reader <- Some newReader
                    logger.LogInformation("GeoIP database loaded from {Path}", path)
                with ex ->
                    logger.LogWarning(ex, "Failed to load GeoIP database from {Path}", path))

    member _.IsAvailable = reader.IsSome

    /// countryCode, countryName, city, lat, lon
    member _.TryLookup(ip: string) =
        match reader with
        | None -> None
        | Some r ->
            try
                let city = r.City(ip: string)
                Some(
                    Option.ofObj city.Country.IsoCode,
                    Option.ofObj city.Country.Name,
                    Option.ofObj city.City.Name,
                    Option.ofNullable city.Location.Latitude,
                    Option.ofNullable city.Location.Longitude)
            with _ -> None

/// Work queues shared between request handlers and background workers.
type WorkQueues() =
    member val GeoQueue: Channel<int64 * string> = Channel.CreateUnbounded<int64 * string>()
    member val TitleQueue: Channel<int64 * string> = Channel.CreateUnbounded<int64 * string>()
    member val WebhookSignal: SemaphoreSlim = new SemaphoreSlim(0)

/// Resolves geolocation for recorded visits.
type GeoWorker(db: Db, geo: GeoIpService, queues: WorkQueues, logger: ILogger<GeoWorker>) =
    inherit BackgroundService()

    member private _.Resolve(visitId: int64, ip: string) =
        task {
            try
                match geo.TryLookup ip with
                | Some(cc, cn, city, lat, lon) -> do! VisitRepo.setGeo db visitId cc cn city lat lon
                | None -> do! VisitRepo.markGeoResolved db visitId
            with ex ->
                logger.LogWarning(ex, "Failed to geolocate visit {VisitId}", visitId)
        }

    override this.ExecuteAsync(ct: CancellationToken) =
        task {
            // Catch up on visits left unresolved by previous runs.
            try
                if geo.IsAvailable then
                    let! pending = VisitRepo.listPendingGeo db 1000
                    for visitId, ip in pending do
                        do! this.Resolve(visitId, ip)
            with ex ->
                logger.LogWarning(ex, "Geo catch-up scan failed")

            try
                while not ct.IsCancellationRequested do
                    let! visitId, ip = queues.GeoQueue.Reader.ReadAsync(ct)
                    do! this.Resolve(visitId, ip)
            with :? OperationCanceledException ->
                ()
        }

/// Fetches page titles for newly created short URLs.
type TitleWorker(db: Db, cfg: AppConfig, httpFactory: IHttpClientFactory, queues: WorkQueues, logger: ILogger<TitleWorker>) =
    inherit BackgroundService()

    static let titleRegex =
        Regex(@"<title[^>]*>\s*(?<t>[^<]{1,512})", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

    member _.TryFetchTitle(url: string, ct: CancellationToken) : Task<string option> =
        task {
            try
                use client = httpFactory.CreateClient("titles")
                use request = new HttpRequestMessage(HttpMethod.Get, url)
                request.Headers.TryAddWithoutValidation("User-Agent", "shortlink-title-resolver/1.0") |> ignore
                use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                let contentType = response.Content.Headers.ContentType
                let isHtml =
                    not (isNull contentType)
                    && not (isNull contentType.MediaType)
                    && contentType.MediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                if not response.IsSuccessStatusCode || not isHtml then
                    return None
                else
                    use! stream = response.Content.ReadAsStreamAsync(ct)
                    let buffer = Array.zeroCreate<byte> 65536
                    let mutable read = 0
                    let mutable finished = false
                    while not finished && read < buffer.Length do
                        let! n = stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct)
                        if n = 0 then finished <- true else read <- read + n
                    let html = Encoding.UTF8.GetString(buffer, 0, read)
                    let m = titleRegex.Match html
                    if m.Success then
                        let title = Net.WebUtility.HtmlDecode(m.Groups.["t"].Value.Trim())
                        return if title = "" then None else Some title
                    else
                        return None
            with _ ->
                return None
        }

    override this.ExecuteAsync(ct: CancellationToken) =
        task {
            if cfg.AutoResolveTitles then
                try
                    while not ct.IsCancellationRequested do
                        let! shortUrlId, url = queues.TitleQueue.Reader.ReadAsync(ct)
                        let! title = this.TryFetchTitle(url, ct)
                        match title with
                        | Some title ->
                            try do! ShortUrlRepo.setResolvedTitle db shortUrlId title
                            with ex -> logger.LogWarning(ex, "Failed to store title for {Id}", shortUrlId)
                        | None -> ()
                with :? OperationCanceledException ->
                    ()
        }

module WebhookEvents =

    /// Fan a payload out to all webhooks subscribed to the event.
    let publish (db: Db) (queues: WorkQueues) (eventSlug: string) (data: 'T) : Task<unit> =
        task {
            let! hooks = WebhookRepo.listForEvent db eventSlug
            if not hooks.IsEmpty then
                let payload =
                    Json.serialize
                        {| ``event`` = eventSlug
                           occurredAt = DateTime.UtcNow
                           data = data |}
                for hook in hooks do
                    do! WebhookRepo.enqueueDelivery db hook.Id eventSlug payload
                if queues.WebhookSignal.CurrentCount = 0 then
                    queues.WebhookSignal.Release() |> ignore
        }

/// Delivers queued webhook payloads with retries and HMAC signatures.
type WebhookWorker(db: Db, httpFactory: IHttpClientFactory, queues: WorkQueues, logger: ILogger<WebhookWorker>) =
    inherit BackgroundService()

    let maxAttempts = 6

    let sign (secret: string) (payload: string) =
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret))
        let hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))
        "sha256=" + (Convert.ToHexString(hash).ToLowerInvariant())

    member private _.Deliver(delivery: WebhookDeliveryRow, hook: WebhookRow, ct: CancellationToken) =
        task {
            try
                use client = httpFactory.CreateClient("webhooks")
                use request = new HttpRequestMessage(HttpMethod.Post, hook.Url)
                request.Content <- new StringContent(delivery.Payload, Encoding.UTF8, "application/json")
                request.Headers.TryAddWithoutValidation("X-Shortlink-Event", delivery.Event) |> ignore
                request.Headers.TryAddWithoutValidation("X-Shortlink-Signature", sign hook.Secret delivery.Payload)
                |> ignore
                use! response = client.SendAsync(request, ct)
                if response.IsSuccessStatusCode then
                    do! WebhookRepo.markDelivered db delivery.Id
                else
                    do!
                        WebhookRepo.markFailedAttempt db delivery.Id delivery.Attempts maxAttempts
                            $"HTTP {int response.StatusCode}"
            with
            | :? OperationCanceledException -> ()
            | ex ->
                logger.LogWarning(ex, "Webhook delivery {Id} failed", delivery.Id)
                do! WebhookRepo.markFailedAttempt db delivery.Id delivery.Attempts maxAttempts ex.Message
        }

    override this.ExecuteAsync(ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    // Wake up when new deliveries are enqueued, or poll for retries.
                    let! _ = queues.WebhookSignal.WaitAsync(TimeSpan.FromSeconds 15.0, ct)
                    let! due = WebhookRepo.dueDeliveries db 50
                    for delivery, hook in due do
                        do! this.Deliver(delivery, hook, ct)
            with :? OperationCanceledException ->
                ()
        }

/// Downloads and refreshes the GeoLite2 city database when a license key is configured.
type GeoDbUpdater(cfg: AppConfig, geo: GeoIpService, httpFactory: IHttpClientFactory, logger: ILogger<GeoDbUpdater>) =
    inherit BackgroundService()

    member private _.Download(licenseKey: string, ct: CancellationToken) =
        task {
            let url =
                "https://download.maxmind.com/app/geoip_download"
                + $"?edition_id=GeoLite2-City&license_key={Uri.EscapeDataString licenseKey}&suffix=tar.gz"
            use client = httpFactory.CreateClient("geoip")
            use! response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            response.EnsureSuccessStatusCode() |> ignore
            use! stream = response.Content.ReadAsStreamAsync(ct)
            use gzip = new GZipStream(stream, CompressionMode.Decompress)
            use tar = new TarReader(gzip)
            let mutable entry = tar.GetNextEntry()
            let mutable extracted = false
            while not extracted && not (isNull entry) do
                if entry.Name.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase) then
                    Directory.CreateDirectory cfg.DataDir |> ignore
                    let target = AppConfig.geoDbPath cfg
                    let tmp = target + ".tmp"
                    do! entry.ExtractToFileAsync(tmp, overwrite = true, cancellationToken = ct)
                    File.Move(tmp, target, overwrite = true)
                    extracted <- true
                else
                    entry <- tar.GetNextEntry()
            return extracted
        }

    override this.ExecuteAsync(ct: CancellationToken) =
        task {
            geo.Reload()
            match cfg.GeoLiteLicenseKey with
            | None ->
                if not geo.IsAvailable then
                    logger.LogInformation(
                        "No GeoLite2 license key configured; visits will not be geolocated. "
                        + "Set SHORTLINK_GEOLITE_LICENSE_KEY to enable geolocation.")
            | Some key ->
                try
                    while not ct.IsCancellationRequested do
                        let dbPath = AppConfig.geoDbPath cfg
                        let stale =
                            not (File.Exists dbPath)
                            || File.GetLastWriteTimeUtc dbPath < DateTime.UtcNow.AddDays(-7.0)
                        if stale then
                            try
                                let! ok = this.Download(key, ct)
                                if ok then geo.Reload()
                                else logger.LogWarning("GeoLite2 download did not contain an .mmdb file")
                            with
                            | :? OperationCanceledException -> ()
                            | ex -> logger.LogWarning(ex, "GeoLite2 download failed")
                        do! Task.Delay(TimeSpan.FromHours 12.0, ct)
                with :? OperationCanceledException ->
                    ()
        }
