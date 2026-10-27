# Akeldov.Data.ClickHouse

Reads and inserts mapped models with the official `ClickHouse.Driver` ADO.NET API. Add a
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

connection.Create<User>(new User { Id = Guid.NewGuid(), Name = "Alice", Active = true });
await connection.CreateAsync(new User { Id = Guid.NewGuid(), Name = "Bob" }, cancellationToken);

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
Use the SDK directly for bulk inserts, aggregation, joins, sorting, paging, and
ClickHouse-specific types or settings outside this mapped select API.

The caller owns the connection. A select opens a closed connection and closes
it after completion or failure; an already open connection remains open.
Each select disposes its command and reader. The async API uses OpenAsync,
ExecuteReaderAsync, and ReadAsync and passes cancellation throughout.

`Create<T>(entity)` returns `void`; `CreateAsync<T>` returns `Task`. Each call
inserts a row and its `Reference` entities into existing tables using every property marked with `Column`.
Properties must have public getters and setters. Values use the same typed
parameters as predicates; null values become SQL `NULL`. Enums use their numeric
values. Unmapped properties are omitted. Generated/default columns are not
inferred and generated values are not returned. Create preserves connection
ownership on success and failure; CreateAsync passes cancellation to the driver.

Both methods save related entities using `Key`, `ForeignKey` and `Reference`:

```csharp
var order = new Order
{
    Id = 100,
    Customer = new Customer { Id = 42 }
};
connection.Create(order); // Inserts Customer first; order.CustomerId becomes 42.
// Alternatively: await connection.CreateAsync(order, cancellationToken);

[Table("analytics", "customers")]
public class Customer
{
    [Key, Column("id")] public int Id { get; set; }
}

[Table("analytics", "orders")]
public class Order
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("customer_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))]
    public int CustomerId { get; set; }
    [Reference(nameof(CustomerId))] public Customer? Customer { get; set; }
}
```

- Supply keys before calling either method. A referenced key must have `Key` and
  `Column` and must be non-null. The local foreign key must have `Column` and
  `ForeignKey`; navigation properties must not have `Column`.
- Non-null references are treated as new rows. Their keys replace the supplied
  foreign keys. To use an existing row, leave the navigation null and supply its FK.
- Nested references are inserted before their owners. A shared object instance is
  inserted once per call; distinct instances with equal keys are separate inserts.
- The complete graph, mappings and parameter values are validated before opening
  the connection or sending any command. Cycles and invalid relationships fail
  locally. Each entity uses its mapped database and table.
- Commands execute sequentially without a graph-wide transaction. On failure or
  cancellation, earlier inserts remain in ClickHouse; changed FK properties in
  memory are restored to their original values. Retrying the entire graph can
  therefore duplicate previously inserted rows.
- `CreateAsync` awaits each insert and checks cancellation before the next command.
  Both methods preserve the connection's original open/closed state.
- These attributes describe model relationships; this API does not create database
  constraints, generate keys, load navigations or save collections.

Run local tests without a server:

```powershell
dotnet test tests/Akeldov.Data.ClickHouse.Tests/Akeldov.Data.ClickHouse.Tests.csproj
```

These tests use the real driver's HTTP transport, parameter rewriting, and
RowBinaryWithNamesAndTypes decoder with an in-memory HTTP handler, as well as
query construction tests. They cover typed bindings, projection, nullable
mapping, connection ownership, reader disposal, errors, and cancellation, as well
as reference ordering, shared objects, FK propagation and restoration, graph
validation, and cancellation during graph insertion.

Run integration tests against an isolated Docker container (Linux containers):

```powershell
dotnet test tests/Akeldov.Data.ClickHouse.IntegrationTests/Akeldov.Data.ClickHouse.IntegrationTests.csproj
```

Integration tests use `clickhouse/clickhouse-server:25.8` and check UUID,
DateTime64 and decimal round trips, C# predicate results including nullable
column comparisons, text parameters, identifier escaping, empty results, and
connection state after successful and failed queries. Create tests also check
nested/shared references and retained dependency rows after a later insert fails.
