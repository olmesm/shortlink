namespace Shortlink.Web

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Shortlink.Core
open Shortlink.Data

/// Visit capture: privacy-aware extraction of request data plus webhook fan-out.
module Tracking =

    let private uaParser = UAParser.Parser.GetDefault()

    let private botRegex =
        Regex(
            @"bot|crawl|spider|slurp|curl|wget|python-requests|httpclient|headless|preview|scan|monitor|facebookexternalhit|whatsapp|telegrambot|skypeuripreview|bingpreview",
            RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

    let isBot (userAgent: string option) =
        match userAgent with
        | None -> false
        | Some ua -> botRegex.IsMatch ua

    let private headerValue (ctx: HttpContext) (name: string) : string option =
        match ctx.Request.Headers.TryGetValue name with
        | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace(v.[0])) -> Some(string v.[0])
        | _ -> None

    let remoteIp (ctx: HttpContext) : string option =
        match ctx.Connection.RemoteIpAddress with
        | null -> None
        | ip -> Some(ip.ToString())

    /// Should this request be excluded from tracking because of the
    /// configured skip param (e.g. ?no-track)?
    let shouldSkip (cfg: AppConfig) (ctx: HttpContext) =
        match cfg.TrackSkipParam with
        | Some param -> ctx.Request.Query.ContainsKey param
        | None -> false

    /// Data attached to webhook events about the visited short URL.
    type VisitedShortUrl =
        { ShortCode: string
          Domain: string
          LongUrl: string }

    /// Record a visit (if tracking settings allow it) and fan out webhook events.
    let record
        (ctx: HttpContext)
        (visitType: VisitType)
        (shortUrl: (int64 * VisitedShortUrl) option)
        : Task<unit> =
        task {
            let cfg = svc<AppConfig> ctx
            let db = svc<Db> ctx
            let queues = svc<WorkQueues> ctx

            let skip =
                cfg.DisableTracking
                || (visitType.IsOrphan && not cfg.TrackOrphanVisits)
                || shouldSkip cfg ctx

            if not skip then
                let userAgent = headerValue ctx "User-Agent"
                let referer = headerValue ctx "Referer"

                let browser, os =
                    match userAgent with
                    | None -> None, None
                    | Some ua ->
                        try
                            let parsed = uaParser.Parse ua
                            let family (s: string) =
                                if String.IsNullOrWhiteSpace s || s = "Other" then None else Some s
                            family parsed.UA.Family, family parsed.OS.Family
                        with _ ->
                            None, None

                let ip =
                    if cfg.DisableIpTracking then None
                    else
                        match remoteIp ctx with
                        | Some ip when cfg.AnonymizeIps -> Anonymize.anonymizeIp ip
                        | other -> other

                let visitedUrl =
                    if visitType.IsOrphan then
                        Some(ctx.Request.Scheme + "://" + ctx.Request.Host.Value + ctx.Request.Path.Value + ctx.Request.QueryString.Value)
                    else
                        None

                let visit: NewVisit =
                    { ShortUrlId = shortUrl |> Option.map fst
                      VisitType = visitType.Slug
                      VisitedAt = DateTime.UtcNow
                      Referer = referer
                      UserAgent = userAgent
                      Browser = browser
                      Os = os
                      Device = Some (RedirectRules.detectDevice userAgent).Slug
                      IsBot = isBot userAgent
                      RemoteIp = ip
                      VisitedUrl = visitedUrl }

                let! visitId = VisitRepo.insert db visit

                match ip with
                | Some ip -> queues.GeoQueue.Writer.TryWrite((visitId, ip)) |> ignore
                | None -> ()

                let eventSlug =
                    if visitType.IsOrphan then OrphanVisitRecorded.Slug else VisitRecorded.Slug

                do!
                    WebhookEvents.publish db queues eventSlug
                        {| visitType = visitType.Slug
                           shortUrl = shortUrl |> Option.map snd
                           visitedUrl = visitedUrl
                           referer = referer
                           userAgent = userAgent
                           potentialBot = isBot userAgent |}
        }
