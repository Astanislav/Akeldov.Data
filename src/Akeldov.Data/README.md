# Akeldov.Data core

Contains mapping attributes and shared query infrastructure used by the database
providers. SQL identifiers and parameters are supplied by each provider's dialect.

## Update infrastructure

`UpdateExecutor` is internal infrastructure analogous to `CreateExecutor`:

- `CreateCommand(connection, dialect, entity)` prepares a parameterized SQL
  `UPDATE` without opening the connection.
- `Update(connection, dialect, entity)` executes the command and returns `void`.
- `UpdateAsync(connection, dialect, entity, cancellationToken)` executes it
  asynchronously and returns `Task`.

SQL Server and PostgreSQL expose public `Update` and `UpdateAsync` extensions
backed by this executor. ClickHouse uses the same mapping and command-building
infrastructure with its `ALTER TABLE ... UPDATE` mutation syntax. Kusto exposes
equivalent extensions using its native `.update table` management command.
MongoDB exposes equivalent client extensions backed by `UpdateMany` and `$set`.

The target table comes from `Table`. Every public instance property marked with
`Key` must also have `Column` and contain a non-null value. All keys participate in
the `WHERE` clause, joined with `AND`, so composite keys are supported. Key values
are never included in `SET`; changing primary keys is outside this operation.
Default non-null keys, such as `0` and `Guid.Empty`, are sent as supplied.

Every non-key `Column` is included in `SET`; null values become SQL `NULL`.
Mapped properties require public getters and setters. Missing keys, partially
mapped keys, null keys, models without non-key columns, and unsupported values
fail before the connection is opened or any command is executed. Values use the
same normalization as Create, with parameter types provided by the dialect.

Unmapped properties are ignored. `Reference` navigations are not traversed and
no related rows are updated. Mapped foreign keys are written exactly as supplied;
the executor does not change the entity. There is no change tracking, upsert, or
affected-row validation: no matching row is not treated as an error.

The caller owns the connection. A connection opened by the executor is closed on
completion or failure; an already open connection remains open. Commands are
disposed in both paths. The async operation checks cancellation before preparation
and execution, passes it to connection opening and command execution, and awaits
completion before releasing resources. It does not start an explicit transaction.

Core tests use a fake ADO.NET connection and dialect; no database is required:

```powershell
dotnet test tests/Akeldov.Data.Tests/Akeldov.Data.Tests.csproj
```

## Delete infrastructure

`DeleteExecutor` provides the shared, provider-independent implementation for
deleting an entity by its mapped keys:

- `CreateCommand(connection, dialect, entity)` prepares a parameterized SQL
  `DELETE` without opening the connection.
- `Delete(connection, dialect, entity)` executes the command and returns `void`.
- `DeleteAsync(connection, dialect, entity, cancellationToken)` executes it
  asynchronously and returns `Task`.

The target table comes from `Table`. Every public instance property marked with
`Key` must also have `Column` and contain a non-null value. All keys participate
in the `WHERE` clause, joined with `AND`, so composite keys are supported. Key
values use the same normalization and provider-supplied parameters as Create and
Update. Default non-null keys are sent as supplied.

Unmapped properties and `Reference` navigations are ignored. The executor does
not traverse or cascade to related entities, perform an upsert, validate the
affected-row count, or start a transaction. No matching row is not an error.

Connection ownership, disposal, async cancellation, and state preservation match
`UpdateExecutor`. SQL Server and PostgreSQL expose public `Delete` and `DeleteAsync` extensions
backed by this executor. Other database-provider packages do not expose these
extensions yet.
