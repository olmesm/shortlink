namespace Shortlink.Web

open System
open System.Security.Claims
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open Shortlink.Core
open Shortlink.Data

[<AutoOpen>]
module Di =
    /// Resolve a service from the request scope.
    let svc<'T> (ctx: HttpContext) : 'T =
        ctx.RequestServices.GetRequiredService<'T>()

module Passwords =
    let hash (password: string) : string =
        BCrypt.Net.BCrypt.HashPassword(password)

    let verify (password: string) (hash: string) : bool =
        try BCrypt.Net.BCrypt.Verify(password, hash) with _ -> false

module ApiKeys =

    /// Generate a new plaintext API key. Only its hash is stored.
    let generate () : string =
        let bytes = RandomNumberGenerator.GetBytes(32)
        "sl_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let hash (key: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes(key)) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

    let roleOf (row: ApiKeyRow) : ApiKeyRole =
        match row.Role, row.DomainId with
        | "author", _ -> AuthorKey
        | "domain", Some domainId -> DomainKey domainId
        | _ -> AdminKey

    let isUsable (row: ApiKeyRow) (now: DateTime) : bool =
        row.Enabled
        && (match row.ExpiresAt with
            | Some exp -> exp > now
            | None -> true)

module ApiAuth =

    let private readKey (ctx: HttpContext) : string option =
        let header (name: string) =
            match ctx.Request.Headers.TryGetValue name with
            | true, values when values.Count > 0 -> Some(values.[0])
            | _ -> None
        match header "X-Api-Key" with
        | Some k when k <> "" -> Some k
        | _ ->
            match header "Authorization" with
            | Some auth when auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
                Some(auth.Substring(7).Trim())
            | _ -> None

    /// Authenticate the request by API key and pass the key row to the handler.
    let requireApiKey (handler: ApiKeyRow -> HttpHandler) : HttpHandler =
        fun ctx ->
            task {
                match readKey ctx with
                | None ->
                    return! Problems.unauthorized "Expected an API key in the X-Api-Key header." ctx
                | Some key ->
                    let db = svc<Db> ctx
                    let! row = ApiKeyRepo.tryFindByHash db (ApiKeys.hash key)
                    match row with
                    | Some row when ApiKeys.isUsable row DateTime.UtcNow -> return! handler row ctx
                    | _ -> return! Problems.unauthorized "The provided API key is not valid." ctx
            }
            :> Task

    /// Authenticate and require the admin role.
    let requireAdminKey (handler: ApiKeyRow -> HttpHandler) : HttpHandler =
        requireApiKey (fun key ->
            match ApiKeys.roleOf key with
            | AdminKey -> handler key
            | _ -> Problems.forbidden "This operation requires an admin API key.")

/// Cookie-based authentication for the admin dashboard.
module UiAuth =

    let scheme = CookieAuthenticationDefaults.AuthenticationScheme

    type CurrentUser =
        { Id: int64
          Username: string
          Role: UserRole }

        member this.IsAdmin = this.Role = AdminUser

    let signIn (ctx: HttpContext) (user: UserRow) : Task =
        let claims =
            [ Claim(ClaimTypes.NameIdentifier, string user.Id)
              Claim(ClaimTypes.Name, user.Username)
              Claim(ClaimTypes.Role, user.Role) ]
        let identity = ClaimsIdentity(claims, scheme)
        let props = AuthenticationProperties(IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays 14.0)
        ctx.SignInAsync(scheme, ClaimsPrincipal(identity), props)

    let signOut (ctx: HttpContext) : Task = ctx.SignOutAsync(scheme)

    let currentUser (ctx: HttpContext) : CurrentUser option =
        if not (isNull ctx.User) && ctx.User.Identity <> null && ctx.User.Identity.IsAuthenticated then
            let idClaim = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
            let name = ctx.User.FindFirstValue(ClaimTypes.Name)
            let role = ctx.User.FindFirstValue(ClaimTypes.Role)
            match Int64.TryParse(idClaim), UserRole.OfSlug(if isNull role then "user" else role) with
            | (true, id), Some role -> Some { Id = id; Username = name; Role = role }
            | _ -> None
        else
            None

    /// Require a signed-in user; redirect to the login page otherwise.
    let requireUser (handler: CurrentUser -> HttpHandler) : HttpHandler =
        fun ctx ->
            match currentUser ctx with
            | Some user -> handler user ctx
            | None ->
                let returnUrl = Uri.EscapeDataString(ctx.Request.Path.Value + ctx.Request.QueryString.Value)
                Response.redirectTemporarily $"/admin/login?returnUrl={returnUrl}" ctx

    /// Require a signed-in admin.
    let requireAdmin (handler: CurrentUser -> HttpHandler) : HttpHandler =
        requireUser (fun user ->
            if user.IsAdmin then handler user
            else Response.withStatusCode 403 >> Response.ofPlainText "Forbidden: admin access required.")
