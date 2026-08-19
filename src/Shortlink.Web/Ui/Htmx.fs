namespace Shortlink.Web.Ui

open Falco.Markup
open Microsoft.AspNetCore.Http

/// htmx attribute helpers and request detection.
module Htmx =

    let hxGet (url: string) = Attr.create "hx-get" url
    let hxPost (url: string) = Attr.create "hx-post" url
    let hxTarget (selector: string) = Attr.create "hx-target" selector
    let hxSwap (mode: string) = Attr.create "hx-swap" mode
    let hxTrigger (trigger: string) = Attr.create "hx-trigger" trigger
    let hxConfirm (message: string) = Attr.create "hx-confirm" message
    let hxPushUrl = Attr.create "hx-push-url" "true"
    let hxInclude (selector: string) = Attr.create "hx-include" selector
    let hxIndicator (selector: string) = Attr.create "hx-indicator" selector

    /// Was this request issued by htmx (so we should render a fragment)?
    let isHtmx (ctx: HttpContext) =
        match ctx.Request.Headers.TryGetValue "HX-Request" with
        | true, v when v.Count > 0 && string v.[0] = "true" -> true
        | _ -> false

module Format =

    open System

    let dateTime (d: DateTime) = d.ToString("yyyy-MM-dd HH:mm")
    let date (d: DateTime) = d.ToString("yyyy-MM-dd")

    let count (n: int64) =
        if n >= 1_000_000L then $"%.1f{float n / 1_000_000.0}M"
        elif n >= 10_000L then $"%.1f{float n / 1_000.0}k"
        else string n
