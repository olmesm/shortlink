namespace Shortlink.Web.Ui

open System
open System.Globalization
open System.Net
open System.Text
open Falco.Markup

/// Server-rendered SVG charts (no client-side JS needed).
module Charts =

    let private inv (v: float) = v.ToString("0.##", CultureInfo.InvariantCulture)

    /// Daily visit counts as a filled line chart. Fills gaps between days with zeroes.
    let visitsPerDay (series: (string * int64) list) : XmlNode =
        let width, height = 720.0, 200.0
        let padL, padR, padT, padB = 40.0, 10.0, 10.0, 22.0

        // Expand to a contiguous day range so gaps show as zero.
        let points =
            match series with
            | [] -> []
            | _ ->
                let parse (s: string) = DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                let byDay = series |> List.map (fun (d, c) -> parse d, c) |> Map.ofList
                let minDay = series |> List.map (fst >> parse) |> List.min
                let maxDay = series |> List.map (fst >> parse) |> List.max
                let maxDay = if maxDay - minDay < TimeSpan.FromDays 6.0 then minDay.AddDays 6.0 else maxDay
                [ let mutable day = minDay
                  while day <= maxDay do
                      yield day, (byDay.TryFind day |> Option.defaultValue 0L)
                      day <- day.AddDays 1.0 ]

        match points with
        | [] ->
            Elem.div [ Attr.class' "muted" ] [ Text.raw "No visits recorded in this period yet." ]
        | points ->
            let maxY = points |> List.map snd |> List.max |> max 1L |> float
            let n = points.Length
            let plotW = width - padL - padR
            let plotH = height - padT - padB
            let x i = padL + (if n = 1 then plotW / 2.0 else plotW * float i / float (n - 1))
            let y (v: int64) = padT + plotH - plotH * float v / maxY

            let sb = StringBuilder()
            sb.Append($"<svg viewBox=\"0 0 {inv width} {inv height}\" xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"Visits per day\">")
            |> ignore

            // Horizontal gridlines + y labels
            for gy in [ 0.0; 0.5; 1.0 ] do
                let value = maxY * (1.0 - gy)
                let yy = padT + plotH * gy
                sb.Append($"<line x1=\"{inv padL}\" y1=\"{inv yy}\" x2=\"{inv (width - padR)}\" y2=\"{inv yy}\" stroke=\"#222738\" stroke-width=\"1\"/>")
                  .Append($"<text x=\"{inv (padL - 6.0)}\" y=\"{inv (yy + 4.0)}\" font-size=\"11\" fill=\"#6b7385\" text-anchor=\"end\">{int64 value}</text>")
                |> ignore

            // Area + line
            let linePath =
                points
                |> List.mapi (fun i (_, v) ->
                    let cmd = if i = 0 then "M" else "L"
                    $"{cmd}{inv (x i)},{inv (y v)}")
                |> String.concat " "
            let areaPath =
                linePath
                + $" L{inv (x (n - 1))},{inv (padT + plotH)} L{inv (x 0)},{inv (padT + plotH)} Z"
            sb.Append($"<path d=\"{areaPath}\" fill=\"rgba(129,140,248,0.16)\" stroke=\"none\"/>")
              .Append($"<path d=\"{linePath}\" fill=\"none\" stroke=\"#818cf8\" stroke-width=\"2\"/>")
            |> ignore

            // Dots with tooltips
            for i, (day, v) in List.indexed points do
                let label = WebUtility.HtmlEncode(day.ToString("yyyy-MM-dd"))
                sb.Append($"<circle cx=\"{inv (x i)}\" cy=\"{inv (y v)}\" r=\"2.5\" fill=\"#818cf8\"><title>{label}: {v}</title></circle>")
                |> ignore

            // Sparse x labels
            let labelEvery = max 1 (n / 8)
            for i, (day, _) in List.indexed points do
                if i % labelEvery = 0 || i = n - 1 then
                    let label = WebUtility.HtmlEncode(day.ToString("MM-dd"))
                    sb.Append($"<text x=\"{inv (x i)}\" y=\"{inv (height - 6.0)}\" font-size=\"10\" fill=\"#6b7385\" text-anchor=\"middle\">{label}</text>")
                    |> ignore

            sb.Append("</svg>") |> ignore
            Text.raw (sb.ToString())

    /// Horizontal bar list (label + count), scaled to the max value.
    let barList (rows: (string * int64) list) : XmlNode =
        match rows with
        | [] -> Elem.div [ Attr.class' "muted" ] [ Text.raw "No data yet." ]
        | rows ->
            let maxV = rows |> List.map snd |> List.max |> max 1L |> float
            Elem.table
                []
                [ Elem.tbody
                      []
                      [ for label, value in rows do
                            let pct = float value / maxV * 100.0
                            Elem.tr
                                []
                                [ Elem.td [ Attr.style "width:35%" ] [ Text.enc label ]
                                  Elem.td
                                      []
                                      [ Elem.div
                                            [ Attr.style
                                                  $"background:rgba(129,140,248,0.35);border-radius:4px;height:1.1rem;width:{inv pct}%%;min-width:2px" ]
                                            [] ]
                                  Elem.td [ Attr.style "width:4rem;text-align:right" ] [ Text.raw (string value) ] ] ] ]
