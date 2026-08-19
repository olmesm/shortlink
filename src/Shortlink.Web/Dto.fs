namespace Shortlink.Web

open System
open Shortlink.Data

/// JSON representations shared by the REST API, webhook payloads and the
/// dashboard. Fields are PascalCase per F# convention; the serializer's
/// camelCase policy shapes the wire format.
type VisitsSummaryDto =
    { Total: int64
      NonBots: int64
      Bots: int64 }

type ShortUrlMetaDto =
    { ValidSince: DateTime option
      ValidUntil: DateTime option
      MaxVisits: int64 option }

type ShortUrlDto =
    { ShortCode: string
      ShortUrl: string
      Domain: string
      LongUrl: string
      Title: string option
      DateCreated: DateTime
      Tags: string list
      Meta: ShortUrlMetaDto
      VisitsSummary: VisitsSummaryDto
      ForwardQuery: bool
      Crawlable: bool
      RedirectStatus: int }

module Dto =

    let shortUrlFor (cfg: AppConfig) (authority: string) (shortCode: string) =
        AppConfig.shortUrlBase cfg authority + "/" + shortCode

    let shortUrl (cfg: AppConfig) (tags: string list) (d: ShortUrlDetail) : ShortUrlDto =
        { ShortCode = d.ShortCode
          ShortUrl = shortUrlFor cfg d.Authority d.ShortCode
          Domain = d.Authority
          LongUrl = d.LongUrl
          Title = d.Title
          DateCreated = d.CreatedAt
          Tags = tags
          Meta =
            { ValidSince = d.ValidSince
              ValidUntil = d.ValidUntil
              MaxVisits = d.MaxVisits }
          VisitsSummary =
            { Total = d.VisitCount
              NonBots = d.VisitCount - d.BotVisitCount
              Bots = d.BotVisitCount }
          ForwardQuery = d.ForwardQuery
          Crawlable = d.Crawlable
          RedirectStatus = d.RedirectStatus }
