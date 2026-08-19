namespace Shortlink.Data

open System

[<CLIMutable>]
type UserRow =
    { Id: int64
      Username: string
      PasswordHash: string
      Role: string
      CreatedAt: DateTime }

[<CLIMutable>]
type DomainRow =
    { Id: int64
      Authority: string
      BaseUrlRedirect: string option
      Regular404Redirect: string option
      InvalidShortUrlRedirect: string option
      IsDefault: bool
      CreatedAt: DateTime }

[<CLIMutable>]
type ShortUrlRow =
    { Id: int64
      ShortCode: string
      DomainId: int64
      LongUrl: string
      Title: string option
      TitleWasAutoResolved: bool
      RedirectStatus: int
      ForwardQuery: bool
      Crawlable: bool
      MaxVisits: int64 option
      ValidSince: DateTime option
      ValidUntil: DateTime option
      AuthorUserId: int64 option
      AuthorApiKeyId: int64 option
      CreatedAt: DateTime }

/// Short URL row enriched with joined data for lists and API payloads.
[<CLIMutable>]
type ShortUrlDetail =
    { Id: int64
      ShortCode: string
      DomainId: int64
      Authority: string
      LongUrl: string
      Title: string option
      TitleWasAutoResolved: bool
      RedirectStatus: int
      ForwardQuery: bool
      Crawlable: bool
      MaxVisits: int64 option
      ValidSince: DateTime option
      ValidUntil: DateTime option
      AuthorUserId: int64 option
      AuthorApiKeyId: int64 option
      CreatedAt: DateTime
      VisitCount: int64
      BotVisitCount: int64 }

[<CLIMutable>]
type TagRow = { Id: int64; Name: string }

[<CLIMutable>]
type TagStatsRow =
    { Id: int64
      Name: string
      ShortUrlCount: int64
      VisitCount: int64 }

[<CLIMutable>]
type RedirectRuleRow =
    { Id: int64
      ShortUrlId: int64
      Priority: int
      LongUrl: string }

[<CLIMutable>]
type RedirectConditionRow =
    { Id: int64
      RuleId: int64
      CondType: string
      MatchKey: string option
      MatchValue: string }

[<CLIMutable>]
type VisitRow =
    { Id: int64
      ShortUrlId: int64 option
      VisitType: string
      VisitedAt: DateTime
      Referer: string option
      UserAgent: string option
      Browser: string option
      Os: string option
      Device: string option
      IsBot: bool
      RemoteIp: string option
      CountryCode: string option
      CountryName: string option
      City: string option
      Latitude: float option
      Longitude: float option
      VisitedUrl: string option
      GeoResolved: bool }

[<CLIMutable>]
type ApiKeyRow =
    { Id: int64
      KeyHash: string
      Name: string option
      Role: string
      DomainId: int64 option
      Enabled: bool
      ExpiresAt: DateTime option
      CreatedAt: DateTime }

[<CLIMutable>]
type WebhookRow =
    { Id: int64
      Name: string
      Url: string
      Secret: string
      Events: string
      Enabled: bool
      CreatedAt: DateTime }

[<CLIMutable>]
type WebhookDeliveryRow =
    { Id: int64
      WebhookId: int64
      Event: string
      Payload: string
      Attempts: int
      NextAttemptAt: DateTime
      Status: string
      LastError: string option
      CreatedAt: DateTime }

[<CLIMutable>]
type CountRow = { Label: string option; Count: int64 }

[<CLIMutable>]
type ShortUrlTagRow = { ShortUrlId: int64; Name: string }

[<CLIMutable>]
type IdUrlRow = { Id: int64; LongUrl: string }

[<CLIMutable>]
type IdIpRow = { Id: int64; RemoteIp: string }
