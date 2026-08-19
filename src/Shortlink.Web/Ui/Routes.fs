namespace Shortlink.Web.Ui

open Falco.Routing

/// All dashboard endpoints in one place.
module Routes =

    let endpoints: Falco.HttpEndpoint list =
        [ get "/admin" OverviewUi.overview
          get "/admin/login" AuthUi.loginForm
          post "/admin/login" AuthUi.login
          post "/admin/logout" AuthUi.logout

          get "/admin/short-urls" ShortUrlsUi.list
          get "/admin/short-urls/new" ShortUrlsUi.createFormPage
          post "/admin/short-urls/new" ShortUrlsUi.create
          get "/admin/short-urls/{id}/edit" ShortUrlsUi.editFormPage
          post "/admin/short-urls/{id}/edit" ShortUrlsUi.edit
          post "/admin/short-urls/{id}/rules/add" ShortUrlsUi.addRule
          post "/admin/short-urls/{id}/rules/delete" ShortUrlsUi.deleteRule
          post "/admin/short-urls/{id}/delete" ShortUrlsUi.deleteShortUrl
          post "/admin/short-urls/{id}/visits/delete" ShortUrlsUi.deleteVisits
          get "/admin/short-urls/{id}/visits" VisitsUi.shortUrlVisits

          get "/admin/visits/orphan" VisitsUi.orphanVisits
          post "/admin/visits/orphan/delete" VisitsUi.deleteOrphan

          get "/admin/tags" TagsUi.list
          post "/admin/tags/rename" TagsUi.rename
          post "/admin/tags/delete" TagsUi.delete

          get "/admin/domains" DomainsUi.list
          post "/admin/domains" DomainsUi.create
          post "/admin/domains/{id}/redirects" DomainsUi.setRedirects
          post "/admin/domains/{id}/delete" DomainsUi.delete

          get "/admin/api-keys" ApiKeysUi.list
          post "/admin/api-keys" ApiKeysUi.create
          post "/admin/api-keys/{id}/toggle" ApiKeysUi.toggle
          post "/admin/api-keys/{id}/delete" ApiKeysUi.delete

          get "/admin/users" UsersUi.list
          post "/admin/users" UsersUi.create
          post "/admin/users/{id}/role" UsersUi.setRole
          post "/admin/users/{id}/password" UsersUi.setPassword
          post "/admin/users/{id}/delete" UsersUi.delete

          get "/admin/webhooks" WebhooksUi.list
          post "/admin/webhooks" WebhooksUi.create
          post "/admin/webhooks/{id}/toggle" WebhooksUi.toggle
          post "/admin/webhooks/{id}/delete" WebhooksUi.delete ]
