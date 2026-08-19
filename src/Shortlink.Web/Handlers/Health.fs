namespace Shortlink.Web.Handlers

open System.Threading.Tasks
open Dapper
open Falco
open Shortlink.Data
open Shortlink.Web

module Health =

    [<Literal>]
    let Version = "1.0.0"

    /// GET /rest/health — no auth; checks database connectivity.
    let check: HttpHandler =
        fun ctx ->
            task {
                let db = svc<Db> ctx
                let healthy =
                    try
                        use conn = db.CreateConnection()
                        conn.ExecuteScalar<int64>("SELECT 1") |> ignore
                        true
                    with _ ->
                        false
                if healthy then
                    return! Json.respond {| status = "pass"; version = Version |} ctx
                else
                    return!
                        (Response.withStatusCode 503 >> Json.respond {| status = "fail"; version = Version |}) ctx
            }
            :> Task
