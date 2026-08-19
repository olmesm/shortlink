namespace Shortlink.Web

open System
open Shortlink.Data

/// All runtime configuration. Populated from SHORTLINK_* environment
/// variables (see fromEnv), or constructed directly in tests.
type AppConfig =
    { /// Authority (host[:port]) used to build short URLs when no domain is picked.
      DefaultDomain: string
      /// Scheme used when rendering short URLs.
      UseHttps: bool
      DbDialect: Dialect
      ConnectionString: string
      /// Directory for runtime state: SQLite db, GeoIP db, data-protection keys.
      DataDir: string
      ShortCodeLength: int
      DefaultRedirectStatus: int
      AutoResolveTitles: bool
      /// Master switch: when true no visits are recorded at all.
      DisableTracking: bool
      /// Track visits but never record any form of the visitor's IP.
      DisableIpTracking: bool
      /// Anonymize recorded IPs (zero host bits) before storing.
      AnonymizeIps: bool
      /// Requests carrying this query param are redirected but not tracked.
      TrackSkipParam: string option
      /// Track visits to unknown short codes / base URL / other 404s.
      TrackOrphanVisits: bool
      /// Global fallbacks; per-domain values in the DB take precedence.
      BaseUrlRedirect: string option
      Regular404Redirect: string option
      InvalidShortUrlRedirect: string option
      GeoLiteLicenseKey: string option
      InitialAdminUsername: string option
      InitialAdminPassword: string option
      /// Requests per minute allowed on mutating REST endpoints, per client IP.
      RateLimitPerMinute: int
      Port: int }

module AppConfig =

    let geoDbPath (cfg: AppConfig) = IO.Path.Combine(cfg.DataDir, "GeoLite2-City.mmdb")

    let shortUrlBase (cfg: AppConfig) (authority: string) =
        let scheme = if cfg.UseHttps then "https" else "http"
        $"{scheme}://{authority}"

    let private boolVar (get: string -> string option) name (defaultVal: bool) =
        match get name with
        | Some v ->
            match v.Trim().ToLowerInvariant() with
            | "1" | "true" | "yes" | "on" -> true
            | "0" | "false" | "no" | "off" -> false
            | _ -> defaultVal
        | None -> defaultVal

    let private intVar (get: string -> string option) name defaultVal =
        match get name |> Option.bind (fun v -> match Int32.TryParse v with | true, i -> Some i | _ -> None) with
        | Some i -> i
        | None -> defaultVal

    let private strVar (get: string -> string option) name =
        get name |> Option.filter (fun s -> s.Trim() <> "") |> Option.map (fun s -> s.Trim())

    /// Build configuration from a variable lookup (normally environment variables
    /// prefixed with SHORTLINK_).
    let fromLookup (get: string -> string option) : AppConfig =
        let dataDir = strVar get "DATA_DIR" |> Option.defaultValue "./data"
        let dialect =
            match strVar get "DB_DRIVER" |> Option.map (fun s -> s.ToLowerInvariant()) with
            | Some "postgres" | Some "postgresql" | Some "pgsql" -> Postgres
            | _ -> Sqlite
        let connString =
            match strVar get "DB_CONNECTION" with
            | Some cs -> cs
            | None ->
                match dialect with
                | Sqlite ->
                    let path = IO.Path.Combine(dataDir, "shortlink.db")
                    $"Data Source={path}"
                | Postgres -> "Host=localhost;Database=shortlink;Username=shortlink;Password=shortlink"
        let port = intVar get "PORT" 8080

        { DefaultDomain = strVar get "DEFAULT_DOMAIN" |> Option.defaultValue $"localhost:{port}"
          UseHttps = boolVar get "USE_HTTPS" false
          DbDialect = dialect
          ConnectionString = connString
          DataDir = dataDir
          ShortCodeLength = intVar get "SHORT_CODE_LENGTH" Shortlink.Core.ShortCode.defaultLength
          DefaultRedirectStatus = intVar get "REDIRECT_STATUS" 302
          AutoResolveTitles = boolVar get "AUTO_RESOLVE_TITLES" true
          DisableTracking = boolVar get "DISABLE_TRACKING" false
          DisableIpTracking = boolVar get "DISABLE_IP_TRACKING" false
          AnonymizeIps = boolVar get "ANONYMIZE_IPS" true
          TrackSkipParam = strVar get "TRACK_SKIP_PARAM"
          TrackOrphanVisits = boolVar get "TRACK_ORPHAN_VISITS" true
          BaseUrlRedirect = strVar get "BASE_URL_REDIRECT"
          Regular404Redirect = strVar get "REGULAR_404_REDIRECT"
          InvalidShortUrlRedirect = strVar get "INVALID_SHORT_URL_REDIRECT"
          GeoLiteLicenseKey = strVar get "GEOLITE_LICENSE_KEY"
          InitialAdminUsername = strVar get "INITIAL_ADMIN_USERNAME"
          InitialAdminPassword = strVar get "INITIAL_ADMIN_PASSWORD"
          RateLimitPerMinute = intVar get "RATE_LIMIT_PER_MINUTE" 120
          Port = port }

    let fromEnv () : AppConfig =
        fromLookup (fun name ->
            match Environment.GetEnvironmentVariable $"SHORTLINK_{name}" with
            | null | "" -> None
            | v -> Some v)
