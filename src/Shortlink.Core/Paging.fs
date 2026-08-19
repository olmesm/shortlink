namespace Shortlink.Core

/// Shared pagination primitives used by list endpoints and UI tables.
module Paging =

    type Page<'T> =
        { Items: 'T list
          CurrentPage: int
          ItemsPerPage: int
          TotalItems: int64 }

        member this.TotalPages =
            if this.ItemsPerPage <= 0 then 1
            else int ((this.TotalItems + int64 this.ItemsPerPage - 1L) / int64 this.ItemsPerPage)

    let defaultPageSize = 20
    let maxPageSize = 500

    /// Clamp raw paging input into sane bounds.
    let normalize (page: int) (itemsPerPage: int) =
        let page = max 1 page
        let size = if itemsPerPage <= 0 then defaultPageSize else min itemsPerPage maxPageSize
        page, size

    let offset (page: int) (itemsPerPage: int) = (page - 1) * itemsPerPage
