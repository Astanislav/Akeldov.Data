# Akeldov.Data.ClickHouse

Reads mapped models with the official `ClickHouse.Driver` ADO.NET API. Add a
project reference to `Akeldov.Data.ClickHouse.csproj` and import
`Akeldov.Data.ClickHouse`.

```csharp
using Akeldov.Data;
using Akeldov.Data.ClickHouse;
using ClickHouse.Driver.ADO;

using var connection = new ClickHouseConnection(
    "Host=localhost;Port=8123;Database=analytics;Username=default;Password=your-password");

var all = connection.Select<User>();
var minimum = 10;
var matching = connection.Select<User>(row => row.Score >= minimum && row.Active);
var asyncRows = await connection.SelectAsync<User>(row => row.Id == userId, cancellationToken);

[Table("analytics", "users")]
public class User
{
    [Column("user_id")] public Guid Id { get; set; }
    [Column("name")] public string? Name { get; set; }
    [Column("score")] public int? Score { get; set; }
    [Column("is_active")] public bool Active { get; set; }
}
```

`userId` and `cancellationToken` are caller-supplied values. `Table.Schema`
specifies the ClickHouse database; `Table.Name` specifies the table. Only
properties marked with `Column` are selected and populated. Identifiers are
case-sensitive and quoted with backticks; backticks and backslashes are escaped.
Map UUID columns to `Guid`, Bool/UInt8 flags to `bool`, and nullable scalar
columns to nullable properties. Values already matching the property type are
assigned directly; the common mapper converts numeric values, enums, and UTC
dates to `DateTimeOffset` as needed. SQL NULL throws for non-nullable value types.

Filters support `==`, `!=`, `>`, `>=`, `<`, `<=`, `&&`, `||`, `!`, boolean
properties, constants, captured fields/properties, column comparisons, and
nullable scalar checks. Nullable comparisons and negations preserve C# behavior.
Values use driver parameters with explicit ClickHouse types; they are never
interpolated into SQL. The driver rewrites `@p0` placeholders to native
`{p0:Type}` bindings. See the official
[.NET driver documentation](https://clickhouse.com/docs/integrations/csharp).

Supported predicate values: strings, characters, booleans, integer types,
enums, finite float/double values, decimal, Guid, DateTime, DateTimeOffset, and
nullable scalar types. The shared SQL translator widens unsigned integer values;
`ulong` parameters use Decimal(38, 0) without losing integer precision. Decimal
parameters preserve their .NET scale using Decimal(38, scale). Date parameters
use DateTime64(7, 'UTC'); local dates and offsets are converted to UTC, and
unspecified DateTime values are treated as UTC. The target column's precision
and ClickHouse's supported date range still apply.

Binary and TimeSpan predicate values, method calls, arithmetic, narrowing casts,
and non-finite floating-point values throw before execution. Reading other
types is possible when the driver returns values compatible with the model.
Use the SDK directly for inserts, aggregation, joins, sorting, paging, and
ClickHouse-specific types or settings outside this mapped select API.

The caller owns the connection. A select opens a closed connection and closes
it after completion or failure; an already open connection remains open.
Each select disposes its command and reader. The async API uses OpenAsync,
ExecuteReaderAsync, and ReadAsync and passes cancellation throughout.

Run local tests without a server:

```powershell
dotnet test tests/Akeldov.Data.ClickHouse.Tests/Akeldov.Data.ClickHouse.Tests.csproj
```

These tests use the real driver's HTTP transport, parameter rewriting, and
RowBinaryWithNamesAndTypes decoder with an in-memory HTTP handler, as well as
query construction tests. They cover typed bindings, projection, nullable
mapping, connection ownership, reader disposal, errors, and cancellation.

Run integration tests against an isolated Docker container (Linux containers):

```powershell
dotnet test tests/Akeldov.Data.ClickHouse.IntegrationTests/Akeldov.Data.ClickHouse.IntegrationTests.csproj
```

Integration tests use `clickhouse/clickhouse-server:25.8` and check UUID,
DateTime64 and decimal round trips, C# predicate results including nullable
column comparisons, text parameters, identifier escaping, empty results, and
connection state after successful and failed queries.
