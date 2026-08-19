namespace Shortlink.Web

open System
open Falco
open QRCoder

/// QR code rendering for short URLs.
module Qr =

    [<RequireQualifiedAccess>]
    type Format =
        | Png
        | Svg

    type Options =
        { Size: int
          Margin: int
          ErrorCorrection: QRCodeGenerator.ECCLevel
          Format: Format }

    let defaults =
        { Size = 300
          Margin = 1
          ErrorCorrection = QRCodeGenerator.ECCLevel.L
          Format = Format.Png }

    let parseOptions (size: int option) (margin: int option) (level: string option) (format: string option) =
        { Size = size |> Option.map (fun s -> Math.Clamp(s, 50, 1000)) |> Option.defaultValue defaults.Size
          Margin = margin |> Option.map (fun m -> Math.Clamp(m, 0, 20)) |> Option.defaultValue defaults.Margin
          ErrorCorrection =
            match level |> Option.map (fun l -> l.ToUpperInvariant()) with
            | Some "M" -> QRCodeGenerator.ECCLevel.M
            | Some "Q" -> QRCodeGenerator.ECCLevel.Q
            | Some "H" -> QRCodeGenerator.ECCLevel.H
            | _ -> QRCodeGenerator.ECCLevel.L
          Format =
            match format |> Option.map (fun f -> f.ToLowerInvariant()) with
            | Some "svg" -> Format.Svg
            | _ -> Format.Png }

    /// Render a QR code for the given content as an HTTP response.
    let respond (content: string) (opts: Options) : HttpHandler =
        use generator = new QRCodeGenerator()
        use data = generator.CreateQrCode(content, opts.ErrorCorrection)
        match opts.Format with
        | Format.Png ->
            // Module count + margin determines pixels-per-module for the requested size.
            let modules = data.ModuleMatrix.Count + opts.Margin * 2
            let pixelsPerModule = max 1 (opts.Size / max 1 modules)
            use qr = new PngByteQRCode(data)
            let bytes = qr.GetGraphic(pixelsPerModule)
            Response.ofBinary "image/png" [] bytes
        | Format.Svg ->
            use qr = new SvgQRCode(data)
            let svg = qr.GetGraphic(opts.Size)
            Response.ofBinary "image/svg+xml" [] (Text.Encoding.UTF8.GetBytes svg)
