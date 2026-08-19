namespace Shortlink.Web

open System.Text
open Falco

/// RFC 7807 problem+json error responses.
module Problems =

    type ProblemDetails =
        { ``type``: string
          title: string
          detail: string
          status: int }

    let problem (status: int) (problemType: string) (title: string) (detail: string) : HttpHandler =
        let body =
            Json.serialize
                { ``type`` = $"https://shortlink.dev/errors/{problemType}"
                  title = title
                  detail = detail
                  status = status }
        Response.withStatusCode status
        >> Response.ofBinary "application/problem+json; charset=utf-8" [] (Encoding.UTF8.GetBytes body)

    let badRequest detail : HttpHandler =
        problem 400 "invalid-data" "Invalid data" detail

    let unauthorized detail : HttpHandler =
        problem 401 "missing-authentication" "Authentication required" detail

    let forbidden detail : HttpHandler =
        problem 403 "forbidden" "Forbidden" detail

    let notFound detail : HttpHandler =
        problem 404 "not-found" "Not found" detail

    let conflict problemType detail : HttpHandler =
        problem 409 problemType "Conflict" detail
