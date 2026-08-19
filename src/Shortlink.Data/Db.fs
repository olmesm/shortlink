namespace Shortlink.Data

open System
open System.Data.Common
open System.Globalization
open Dapper
open Microsoft.Data.Sqlite
open Npgsql

type Dialect =
    | Sqlite
    | Postgres

/// Connection factory + dialect-specific SQL fragments.
type Db =
    { Dialect: Dialect
      ConnectionString: string }

    member this.CreateConnection() : DbConnection =
        match this.Dialect with
        | Sqlite ->
            let conn = new SqliteConnection(this.ConnectionString)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;"
            cmd.ExecuteNonQuery() |> ignore
            conn :> DbConnection
        | Postgres ->
            let conn = new NpgsqlConnection(this.ConnectionString)
            conn.Open()
            conn :> DbConnection

    /// SQL expression grouping a timestamp column by calendar day (UTC), as 'YYYY-MM-DD'.
    member this.DayExpr(column: string) =
        match this.Dialect with
        | Sqlite -> $"strftime('%%Y-%%m-%%d', {column})"
        | Postgres -> $"to_char({column} AT TIME ZONE 'UTC', 'YYYY-MM-DD')"

    /// Case-insensitive LIKE comparison.
    member this.ILike(column: string, param: string) =
        $"lower({column}) LIKE lower({param})"

module private TypeHandlers =

    /// Store/read DateTime as UTC; parses SQLite TEXT values invariantly.
    type UtcDateTimeHandler() =
        inherit SqlMapper.TypeHandler<DateTime>()

        override _.SetValue(param, value) =
            let utc =
                match value.Kind with
                | DateTimeKind.Utc -> value
                | DateTimeKind.Local -> value.ToUniversalTime()
                | _ -> DateTime.SpecifyKind(value, DateTimeKind.Utc)
            param.Value <- utc

        override _.Parse(value) =
            match value with
            | :? DateTime as dt -> DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc)
            | :? string as s ->
                DateTime.SpecifyKind(
                    DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                    DateTimeKind.Utc)
            | other -> failwith $"Cannot convert {other.GetType().Name} to DateTime"

    type OptionHandler<'T>() =
        inherit SqlMapper.TypeHandler<'T option>()

        override _.SetValue(param, value) =
            param.Value <-
                match value with
                | Some v -> box v
                | None -> box DBNull.Value

        override _.Parse(value) =
            if isNull value || value = box DBNull.Value then None
            else
                match box value with
                | :? 'T as typed -> Some typed
                | _ -> Some(Convert.ChangeType(value, typeof<'T>, CultureInfo.InvariantCulture) :?> 'T)

    type DateTimeOptionHandler() =
        inherit SqlMapper.TypeHandler<DateTime option>()
        let inner = UtcDateTimeHandler()

        override _.SetValue(param, value) =
            match value with
            | Some v -> inner.SetValue(param, v)
            | None -> param.Value <- box DBNull.Value

        override _.Parse(value) =
            if isNull value || value = box DBNull.Value then None
            else Some(inner.Parse value)

module Db =

    let mutable private initialized = false
    let private initLock = obj ()

    /// Register Dapper type handlers; call once at startup.
    let registerTypeHandlers () =
        lock initLock (fun () ->
            if not initialized then
                initialized <- true
                DefaultTypeMap.MatchNamesWithUnderscores <- true
                SqlMapper.RemoveTypeMap(typeof<DateTime>)
                SqlMapper.AddTypeHandler(TypeHandlers.UtcDateTimeHandler())
                SqlMapper.AddTypeHandler(TypeHandlers.DateTimeOptionHandler())
                SqlMapper.AddTypeHandler(TypeHandlers.OptionHandler<string>())
                SqlMapper.AddTypeHandler(TypeHandlers.OptionHandler<int>())
                SqlMapper.AddTypeHandler(TypeHandlers.OptionHandler<int64>())
                SqlMapper.AddTypeHandler(TypeHandlers.OptionHandler<float>())
                SqlMapper.AddTypeHandler(TypeHandlers.OptionHandler<bool>()))

    let create (dialect: Dialect) (connectionString: string) : Db =
        registerTypeHandlers ()
        { Dialect = dialect
          ConnectionString = connectionString }
