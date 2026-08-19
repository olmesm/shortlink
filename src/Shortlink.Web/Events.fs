namespace Shortlink.Web

open Shortlink.Core

/// The short URL a visit event refers to.
type VisitedShortUrl =
    { ShortCode: string
      Domain: string
      LongUrl: string }

/// What a visit event carries to webhook subscribers.
type VisitEventPayload =
    { VisitType: string
      ShortUrl: VisitedShortUrl option
      VisitedUrl: string option
      Referer: string option
      UserAgent: string option
      PotentialBot: bool }

/// Integration events published by the application. Typed end-to-end: the
/// event kind and its payload shape can no longer drift apart per call site.
type DomainEvent =
    | UrlCreated of ShortUrlDto
    | VisitRecorded of VisitEventPayload
    | OrphanVisitRecorded of VisitEventPayload

    /// The webhook subscription this event maps to.
    member this.Kind =
        match this with
        | UrlCreated _ -> WebhookEvent.UrlCreated
        | VisitRecorded _ -> WebhookEvent.VisitRecorded
        | OrphanVisitRecorded _ -> WebhookEvent.OrphanVisitRecorded

    /// The signed JSON body delivered to webhook endpoints.
    member this.ToDeliveryPayload(occurredAt: System.DateTime) =
        let envelope (data: 'T) =
            Json.serialize
                {| Event = this.Kind.Slug
                   OccurredAt = occurredAt
                   Data = data |}

        match this with
        | UrlCreated dto -> envelope dto
        | VisitRecorded payload
        | OrphanVisitRecorded payload -> envelope payload
