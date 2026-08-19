namespace Shortlink.Core

/// Strongly-typed identifiers. Persistence rows carry raw int64s; everything
/// above the row level speaks in these, so a domain id can never be passed
/// where a short-url id is expected.
[<AutoOpen>]
module Ids =

    [<Struct>]
    type ShortUrlId =
        | ShortUrlId of int64

        member this.Value = let (ShortUrlId v) = this in v

    [<Struct>]
    type DomainId =
        | DomainId of int64

        member this.Value = let (DomainId v) = this in v

    [<Struct>]
    type VisitId =
        | VisitId of int64

        member this.Value = let (VisitId v) = this in v

    [<Struct>]
    type UserId =
        | UserId of int64

        member this.Value = let (UserId v) = this in v

    [<Struct>]
    type ApiKeyId =
        | ApiKeyId of int64

        member this.Value = let (ApiKeyId v) = this in v

    [<Struct>]
    type WebhookId =
        | WebhookId of int64

        member this.Value = let (WebhookId v) = this in v
