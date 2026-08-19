namespace Shortlink.Web

open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Shortlink.Core
open Shortlink.Data

/// Application services: orchestration between the domain core and the
/// repositories. All validation lives in Shortlink.Core — these functions
/// only sequence IO around already-validated values.
module Services =

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

    /// The lifetime stored on a row. Values were validated on the way in, so
    /// this is a plain projection.
    let lifetimeOfRow (row: ShortUrlRow) : Lifetime =
        { ValidSince = row.ValidSince
          ValidUntil = row.ValidUntil
          MaxVisits = row.MaxVisits }

    /// Resolve the spec's target domain, auto-registering unknown authorities.
    let private resolveTargetDomain (db: Db) (domain: DomainAuthority option) : Task<Result<DomainRow, ShortUrlError>> =
        task {
            match domain with
            | None ->
                let! d = DomainRepo.getDefault db
                return Ok d
            | Some authority ->
                let! existing = DomainRepo.tryGetByAuthority db authority.Value

                match existing with
                | Some d -> return Ok d
                | None ->
                    let! created = DomainRepo.create db authority

                    match created with
                    | Some d -> return Ok d
                    | None ->
                        // Lost a race with a concurrent insert; fetch the winner.
                        let! d = DomainRepo.tryGetByAuthority db authority.Value

                        return
                            d |> Result.requireSome (ShortUrlError.UnknownDomain authority.Value)
        }

    /// Insert with the spec's slug, or retry generated codes until one is free.
    let private insertWithCode
        (db: Db)
        (cfg: AppConfig)
        (spec: ShortUrlSpec)
        (domain: DomainRow)
        (record: ShortCode -> NewShortUrl)
        : Task<Result<ShortUrlId, ShortUrlError>> =
        match spec.CustomSlug with
        | Some slug ->
            ShortUrlRepo.create db (record slug) spec.Tags
            |> TaskResult.mapError (fun InsertShortUrlError.DuplicateShortCode ->
                ShortUrlError.SlugInUse(slug.Value, domain.Authority))
        | None ->
            let codeLength =
                max ShortCode.minLength (spec.CodeLength |> Option.defaultValue cfg.ShortCodeLength)

            let rec tryInsert attemptsLeft =
                task {
                    if attemptsLeft = 0 then
                        return Error ShortUrlError.CodeGenerationExhausted
                    else
                        let! result = ShortUrlRepo.create db (record (ShortCode.generate codeLength)) spec.Tags

                        match result with
                        | Ok id -> return Ok id
                        | Error InsertShortUrlError.DuplicateShortCode -> return! tryInsert (attemptsLeft - 1)
                }

            tryInsert 10

    /// Create a short URL from a validated spec: domain resolution
    /// (auto-registering unknown domains), code generation with collision
    /// retry, atomic insert with tags, async title resolution and event
    /// publication.
    let createShortUrl
        (db: Db)
        (cfg: AppConfig)
        (queues: WorkQueues)
        (author: Choice<UserId, ApiKeyId> option)
        (spec: ShortUrlSpec)
        : Task<Result<ShortUrlDto, ShortUrlError>> =
        taskResult {
            let! domain = resolveTargetDomain db spec.Domain

            let! existing =
                match spec.FindIfExists with
                | true -> ShortUrlRepo.tryFindByLongUrl db (DomainId domain.Id) spec.LongUrl
                | false -> Task.singleton None

            match existing with
            | Some d ->
                let! tags = TagRepo.forShortUrl db (ShortUrlId d.Id)
                return Dto.shortUrl cfg tags d
            | None ->
                let record code : NewShortUrl =
                    { ShortCode = code
                      DomainId = DomainId domain.Id
                      LongUrl = spec.LongUrl
                      Title = spec.Title
                      RedirectStatus = spec.RedirectStatus |> Option.defaultValue cfg.DefaultRedirectStatus
                      ForwardQuery = spec.ForwardQuery |> Option.defaultValue true
                      Crawlable = spec.Crawlable |> Option.defaultValue false
                      Lifetime = spec.Lifetime
                      AuthorUserId =
                        match author with
                        | Some(Choice1Of2 userId) -> Some userId
                        | _ -> None
                      AuthorApiKeyId =
                        match author with
                        | Some(Choice2Of2 keyId) -> Some keyId
                        | _ -> None }

                let! id = insertWithCode db cfg spec domain record

                if cfg.AutoResolveTitles && spec.Title.IsNone then
                    queues.TitleQueue.Writer.TryWrite((id, spec.LongUrl)) |> ignore

                let! detail =
                    ShortUrlRepo.tryGetDetailById db id
                    |> TaskResult.requireSome ShortUrlError.CodeGenerationExhausted

                let dto = Dto.shortUrl cfg (spec.Tags |> List.map TagName.value) detail
                Events.publish queues (UrlCreated dto)
                return dto
        }

    /// Apply a validated edit to an existing short URL.
    let editShortUrl (db: Db) (cfg: AppConfig) (id: ShortUrlId) (current: ShortUrlDetail) (edit: ShortUrlEdit) : Task<ShortUrlDto> =
        task {
            let update: ShortUrlUpdate =
                { LongUrl = edit.LongUrl
                  Title = edit.Title
                  TitleWasAutoResolved = edit.Title = current.Title && current.TitleWasAutoResolved
                  RedirectStatus = edit.RedirectStatus
                  ForwardQuery = edit.ForwardQuery
                  Crawlable = edit.Crawlable
                  Lifetime = edit.Lifetime }

            let! _ = ShortUrlRepo.update db id update

            match edit.Tags with
            | Some tags -> do! ShortUrlRepo.setTags db id tags
            | None -> ()

            let! updated = ShortUrlRepo.tryGetDetailById db id
            let! tagNames = TagRepo.forShortUrl db id
            // The row was just updated under this id; absence would be a bug.
            return Dto.shortUrl cfg tagNames updated.Value
        }
