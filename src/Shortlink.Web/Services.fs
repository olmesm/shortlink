namespace Shortlink.Web

open System
open System.Threading.Tasks
open Shortlink.Core
open Shortlink.Data

module Task =
    let map (f: 'a -> 'b) (t: Task<'a>) : Task<'b> =
        task {
            let! v = t
            return f v
        }

/// API/UI-facing representation of a short URL.
type VisitsSummaryDto =
    { total: int64
      nonBots: int64
      bots: int64 }

type ShortUrlMetaDto =
    { validSince: DateTime option
      validUntil: DateTime option
      maxVisits: int64 option }

type ShortUrlDto =
    { shortCode: string
      shortUrl: string
      domain: string
      longUrl: string
      title: string option
      dateCreated: DateTime
      tags: string list
      meta: ShortUrlMetaDto
      visitsSummary: VisitsSummaryDto
      forwardQuery: bool
      crawlable: bool
      redirectStatus: int }

/// Input for creating a short URL, shared by REST API and dashboard.
type CreateShortUrlInput =
    { LongUrl: string
      CustomSlug: string option
      ShortCodeLength: int option
      Domain: string option
      Title: string option
      Tags: string list
      MaxVisits: int64 option
      ValidSince: DateTime option
      ValidUntil: DateTime option
      ForwardQuery: bool option
      Crawlable: bool option
      RedirectStatus: int option
      FindIfExists: bool
      AuthorUserId: int64 option
      AuthorApiKeyId: int64 option }

module CreateShortUrlInput =
    let make longUrl =
        { LongUrl = longUrl
          CustomSlug = None
          ShortCodeLength = None
          Domain = None
          Title = None
          Tags = []
          MaxVisits = None
          ValidSince = None
          ValidUntil = None
          ForwardQuery = None
          Crawlable = None
          RedirectStatus = None
          FindIfExists = false
          AuthorUserId = None
          AuthorApiKeyId = None }

module Services =

    let shortUrlFor (cfg: AppConfig) (authority: string) (shortCode: string) =
        AppConfig.shortUrlBase cfg authority + "/" + shortCode

    let toDto (cfg: AppConfig) (tags: string list) (d: ShortUrlDetail) : ShortUrlDto =
        { shortCode = d.ShortCode
          shortUrl = shortUrlFor cfg d.Authority d.ShortCode
          domain = d.Authority
          longUrl = d.LongUrl
          title = d.Title
          dateCreated = d.CreatedAt
          tags = tags
          meta =
            { validSince = d.ValidSince
              validUntil = d.ValidUntil
              maxVisits = d.MaxVisits }
          visitsSummary =
            { total = d.VisitCount
              nonBots = d.VisitCount - d.BotVisitCount
              bots = d.BotVisitCount }
          forwardQuery = d.ForwardQuery
          crawlable = d.Crawlable
          redirectStatus = d.RedirectStatus }

    /// Resolve the domain row for an incoming request Host (fall back to the default domain).
    let resolveRequestDomain (db: Db) (hostAuthority: string) : Task<DomainRow> =
        task {
            let! byHost = DomainRepo.tryGetByAuthority db (hostAuthority.ToLowerInvariant())
            match byHost with
            | Some d -> return d
            | None -> return! DomainRepo.getDefault db
        }

    /// Resolve an explicitly named domain (API "domain" param). None → default domain.
    let resolveNamedDomain (db: Db) (authority: string option) : Task<DomainRow option> =
        task {
            match authority with
            | None ->
                let! d = DomainRepo.getDefault db
                return Some d
            | Some a -> return! DomainRepo.tryGetByAuthority db (a.Trim().ToLowerInvariant())
        }

    /// Is the short URL currently allowed to redirect?
    let checkActive (row: ShortUrlRow) (validVisitCount: int64) (now: DateTime) : Result<unit, ExpirationReason> =
        match row.ValidSince with
        | Some since when now < since -> Error NotYetValid
        | _ ->
            match row.ValidUntil with
            | Some until when now > until -> Error NoLongerValid
            | _ ->
                match row.MaxVisits with
                | Some maxV when validVisitCount >= maxV -> Error MaxVisitsReached
                | _ -> Ok()

    /// Validate input pieces that don't need the database.
    let private validateInput (input: CreateShortUrlInput) =
        match Validation.validateLongUrl input.LongUrl with
        | Error e -> Error(DomainErrors.InvalidLongUrl e)
        | Ok longUrl ->
            let slugResult =
                match input.CustomSlug with
                | Some slug -> ShortCode.validateSlug slug |> Result.map Some
                | None -> Ok None
            match slugResult with
            | Error e -> Error(DomainErrors.InvalidSlug e)
            | Ok customSlug ->
                match Validation.normalizeTags input.Tags with
                | Error e -> Error(DomainErrors.InvalidSlug e)
                | Ok tags -> Ok(longUrl, customSlug, tags)

    /// Resolve or auto-register the target domain of a new short URL.
    let private resolveTargetDomain (db: Db) (domain: string option) =
        task {
            match domain with
            | None ->
                let! d = DomainRepo.getDefault db
                return Ok d
            | Some authority ->
                match Validation.validateDomainAuthority authority with
                | Error e -> return Error(DomainErrors.UnknownDomain e)
                | Ok authority ->
                    let! existing = DomainRepo.tryGetByAuthority db authority
                    match existing with
                    | Some d -> return Ok d
                    | None ->
                        let! created = DomainRepo.create db authority
                        match created with
                        | Some d -> return Ok d
                        | None ->
                            // Lost a race with a concurrent insert; fetch the winner.
                            let! d = DomainRepo.tryGetByAuthority db authority
                            match d with
                            | Some d -> return Ok d
                            | None -> return Error(DomainErrors.UnknownDomain authority)
        }

    let private insertWithCode
        (db: Db)
        (cfg: AppConfig)
        (input: CreateShortUrlInput)
        (domainAuthority: string)
        (newRecord: string -> NewShortUrl)
        (customSlug: string option)
        =
        task {
            match customSlug with
            | Some slug ->
                let! result = ShortUrlRepo.insert db (newRecord slug)
                return
                    result
                    |> Result.mapError (fun DuplicateShortCode ->
                        DomainErrors.SlugInUse(slug, domainAuthority))
            | None ->
                let codeLength =
                    max ShortCode.minLength (input.ShortCodeLength |> Option.defaultValue cfg.ShortCodeLength)
                let mutable attempt = 0
                let mutable outcome = Error DomainErrors.CodeGenerationExhausted
                let mutable retry = true
                while retry && attempt < 10 do
                    attempt <- attempt + 1
                    let code = ShortCode.generate codeLength
                    let! result = ShortUrlRepo.insert db (newRecord code)
                    match result with
                    | Ok id ->
                        outcome <- Ok id
                        retry <- false
                    | Error DuplicateShortCode -> ()
                return outcome
        }

    /// Create a short URL end-to-end: validation, domain resolution (auto-registering
    /// unknown domains), code generation with collision retry, tags, async title
    /// resolution and webhook fan-out.
    let createShortUrl
        (db: Db)
        (cfg: AppConfig)
        (queues: WorkQueues)
        (input: CreateShortUrlInput)
        : Task<Result<ShortUrlDto, DomainErrors.CreateShortUrlError>> =
        task {
            match validateInput input with
            | Error e -> return Error e
            | Ok(longUrl, customSlug, tags) ->
                let! domainResult = resolveTargetDomain db input.Domain
                match domainResult with
                | Error e -> return Error e
                | Ok domain ->
                    let! existing =
                        if input.FindIfExists then ShortUrlRepo.tryFindByLongUrl db domain.Id longUrl
                        else Task.FromResult None
                    match existing with
                    | Some d ->
                        let! existingTags = TagRepo.forShortUrl db d.Id
                        return Ok(toDto cfg existingTags d)
                    | None ->
                        let newRecord code : NewShortUrl =
                            { ShortCode = code
                              DomainId = domain.Id
                              LongUrl = longUrl
                              Title = input.Title
                              RedirectStatus =
                                input.RedirectStatus
                                |> Option.bind (RedirectStatus.OfCode >> Option.map (fun s -> s.Code))
                                |> Option.defaultValue cfg.DefaultRedirectStatus
                              ForwardQuery = input.ForwardQuery |> Option.defaultValue true
                              Crawlable = input.Crawlable |> Option.defaultValue false
                              MaxVisits = input.MaxVisits
                              ValidSince = input.ValidSince
                              ValidUntil = input.ValidUntil
                              AuthorUserId = input.AuthorUserId
                              AuthorApiKeyId = input.AuthorApiKeyId }

                        let! inserted = insertWithCode db cfg input domain.Authority newRecord customSlug
                        match inserted with
                        | Error e -> return Error e
                        | Ok id ->
                            let! tagIds = TagRepo.ensure db tags
                            do! TagRepo.setForShortUrl db id tagIds

                            if cfg.AutoResolveTitles && input.Title.IsNone then
                                queues.TitleQueue.Writer.TryWrite((id, longUrl)) |> ignore

                            let! detail = ShortUrlRepo.tryGetDetailById db id
                            match detail with
                            | None -> return Error DomainErrors.CodeGenerationExhausted
                            | Some detail ->
                                let dto = toDto cfg tags detail
                                do! WebhookEvents.publish db queues UrlCreated.Slug dto
                                return Ok dto
        }
