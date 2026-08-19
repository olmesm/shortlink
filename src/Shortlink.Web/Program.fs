module Shortlink.Web.Program

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting

[<EntryPoint>]
let main _args =
    let cfg = AppConfig.fromEnv ()
    let wapp = App.build cfg (fun builder -> builder.WebHost.UseUrls($"http://0.0.0.0:{cfg.Port}") |> ignore)
    wapp.Run()
    0
