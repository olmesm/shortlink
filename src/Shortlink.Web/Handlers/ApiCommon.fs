namespace Shortlink.Web.Handlers

open System
open System.Globalization
open System.Text.Json
open System.Threading.Tasks
open Falco
open Microsoft.AspNetCore.Http
open Shortlink.Core
open Shortlink.Data
open Shortlink.Web

/// Paged REST response envelope.
type PaginationDto =
    { currentPage: int
      pagesCount: int
      itemsPerPage: int
      itemsInCurrentPage: int
      totalItems: int64 }

type PageDto<'T> =
    { data: 'T list
      pagination: PaginationDto }

type VisitDto =
    { date: DateTime
      referer: string option
      userAgent: string option
      browser: string option
      os: string option
      device: string option
      potentialBot: bool
      visitedUrl: string option
      countryCode: string option
      country: string option
      city: string option
      latitude: float option
      longitude: float option }

module Api =

    let pageDto (map: 'a -> 'b) (page: Paging.Page<'a>) : PageDto<'b> =
        { data = page.Items |> List.map map
          pagination =
            { currentPage = page.CurrentPage
              pagesCount = page.TotalPages
              itemsPerPage = page.ItemsPerPage
              itemsInCurrentPage = page.Items.Length
              totalItems = page.TotalItems } }

    let visitDto (v: VisitRow) : VisitDto =
        { date = v.VisitedAt
          referer = v.Referer
          userAgent = v.UserAgent
          browser = v.Browser
          os = v.Os
          device = v.Device
          potentialBot = v.IsBot
          visitedUrl = v.VisitedUrl
          countryCode = v.CountryCode
          country = v.CountryName
          city = v.City
          latitude = v.Latitude
          longitude = v.Longitude }

    /// Parse an ISO-8601 date(-time), always yielding UTC.
    let tryParseDate (s: string) : DateTime option =
        match DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal ||| DateTimeStyles.AssumeUniversal) with
        | true, d -> Some(DateTime.SpecifyKind(d, DateTimeKind.Utc))
        | _ -> None

    let queryInt (q: RequestData) (name: string) : int option =
        q.TryGetString name
        |> Option.bind (fun v ->
            match Int32.TryParse v with
            | true, i -> Some i
            | _ -> None)

    let queryDate (q: RequestData) (name: string) : DateTime option =
        q.TryGetString name |> Option.bind tryParseDate

    let queryBool (q: RequestData) (name: string) : bool =
        match q.TryGetString name |> Option.map (fun v -> v.ToLowerInvariant()) with
        | Some("true" | "1" | "yes") -> true
        | _ -> false

    let visitFiltersFromQuery (q: RequestData) : VisitFilters =
        { StartDate = queryDate q "startDate"
          EndDate = queryDate q "endDate"
          ExcludeBots = queryBool q "excludeBots"
          Page = queryInt q "page" |> Option.defaultValue 1
          ItemsPerPage = queryInt q "itemsPerPage" |> Option.defaultValue Paging.defaultPageSize }

    /// Read and deserialize a JSON request body with the shared options.
    let readJson<'T> (ctx: HttpContext) : Task<Result<'T, string>> =
        task {
            try
                let! parsed = JsonSerializer.DeserializeAsync<'T>(ctx.Request.Body, Json.options)
                if obj.ReferenceEquals(parsed, null) then
                    return Error "The request body cannot be empty."
                else
                    return Ok parsed
            with
            | :? JsonException as ex -> return Error $"Invalid request body: {ex.Message}"
            | :? NotSupportedException as ex -> return Error $"Invalid request body: {ex.Message}"
        }

    /// Handler wrapper: deserialize body or answer 400 problem+json.
    let withJson<'T> (handler: 'T -> HttpHandler) : HttpHandler =
        fun ctx ->
            task {
                let! body = readJson<'T> ctx
                match body with
                | Ok body -> return! handler body ctx
                | Error message -> return! Problems.badRequest message ctx
            }
            :> Task

    // ---- API key scoping ----

    /// Restrict list filters to what an API key may see.
    let applyKeyScope (key: ApiKeyRow) (filters: ShortUrlFilters) : ShortUrlFilters =
        match ApiKeys.roleOf key with
        | AdminKey -> filters
        | AuthorKey -> { filters with AuthorApiKeyId = Some key.Id }
        | DomainKey domainId -> { filters with DomainId = Some domainId }

    /// May this key see/manipulate the given short URL?
    let canAccessShortUrl (key: ApiKeyRow) (detail: ShortUrlDetail) : bool =
        match ApiKeys.roleOf key with
        | AdminKey -> true
        | AuthorKey -> detail.AuthorApiKeyId = Some key.Id
        | DomainKey domainId -> detail.DomainId = domainId

    /// Resolve a short URL by code (+ optional ?domain=) and check key access.
    let findAccessibleShortUrl
        (db: Db)
        (key: ApiKeyRow)
        (code: string)
        (domainAuthority: string option)
        : Task<Result<ShortUrlDetail, HttpHandler>> =
        task {
            let! domain = Services.resolveNamedDomain db domainAuthority
            match domain with
            | None ->
                let requested = defaultArg domainAuthority ""
                return Error(Problems.notFound $"Domain '{requested}' is not registered.")
            | Some domain ->
                let! detail = ShortUrlRepo.tryGetDetail db domain.Id code
                match detail with
                | None ->
                    return Error(Problems.notFound $"No short URL found for code '{code}'.")
                | Some detail when not (canAccessShortUrl key detail) ->
                    // Do not leak existence to keys that cannot see the URL.
                    return Error(Problems.notFound $"No short URL found for code '{code}'.")
                | Some detail -> return Ok detail
        }
