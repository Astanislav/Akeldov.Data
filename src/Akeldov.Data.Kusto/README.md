# Akeldov.Data.Kusto

Reads and inserts mapped models in Azure Data Explorer / Kusto using the official
`Microsoft.Azure.Kusto.Data` SDK. Add a project reference to
`Akeldov.Data.Kusto.csproj` and import `Akeldov.Data.Kusto`.

```csharp
using Akeldov.Data;
using Akeldov.Data.Kusto;
using Kusto.Data;
using Kusto.Data.Net.Client;

var connection = new KustoConnectionStringBuilder("https://your-cluster.kusto.windows.net")
    .WithAadUserPromptAuthentication();
using var client = KustoClientFactory.CreateCslQueryProvider(connection);

var all = client.Select<User>();
var minimum = 10;
var matching = client.Select<User>(row => row.Id >= minimum && row.Active);
var asyncRows = await client.SelectAsync<User>(row => row.Id >= minimum, cancellationToken);

[Table("analytics", "users")]
public class User
{
    [Column("user_id")] public int Id { get; set; }
    [Column("name")] public string? Name { get; set; }
    [Column("active")] public bool Active { get; set; }
}
```

`cancellationToken` is a caller-supplied `CancellationToken`. The first `Table`
argument (`Schema`) specifies the **Kusto database**; the second specifies the
table. Only properties marked with `Column` are projected and populated. Table
and column names are quoted, and column names are case-sensitive. The caller
owns and disposes the query provider; each select disposes its result reader.

Filters support `==`, `!=`, `>`, `>=`, `<`, `<=`, `&&`, `||`, `!`, boolean
properties, constants, captured fields/properties, column comparisons, and
nullable scalar checks. Values are declared with `declare query_parameters`
and passed separately through `ClientRequestProperties`.

Supported predicate types: `string`, `bool`, integer types, enums, `float`,
`double`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, and nullable versions
of value types. `ulong` values must fit in `long`; floating-point parameters
must be finite. `decimal`, `char`, binary/dynamic predicates, method calls,
arithmetic, and narrowing conversions throw `NotSupportedException` before
execution. Aggregations, joins, ordering, and arbitrary KQL are outside this
mapped select API; use the SDK directly for these queries.

Nullable comparisons preserve C# equality and negation behavior. Kusto strings
cannot be null: `row.Name == null` translates to `false`, and `!= null` to
`true`. Use `row.Name == ""` to select empty strings. See Microsoft's
[Kusto null semantics](https://learn.microsoft.com/en-us/kusto/query/scalar-data-types/null-values).

Dates are sent in UTC; `DateTime` values with `Kind.Unspecified` are treated as
UTC. Kusto `datetime` results can populate `DateTimeOffset` properties with a
zero offset. The async API passes cancellation to the SDK and checks it while
mapping results; the SDK's `IDataReader` is read synchronously.

For individual inserts, use a separate administration provider:

```csharp
using var admin = KustoClientFactory.CreateCslAdminProvider(connection);
admin.Create<User>(new User { Id = 10, Name = "Alice", Active = true });
await admin.CreateAsync(new User { Id = 11, Name = "Bob" }, cancellationToken);
```

`Create<T>(entity)` returns `void`; `CreateAsync<T>` returns `Task`. They use
`.ingest inline` with JSON data and an explicit mapping by column name, so model
property order need not match table column order. The table must already exist;
only `Column` properties with public getters and setters are serialized. Supported
types and range checks match the predicate types above. Null strings become
empty strings in Kusto. The caller owns the administration provider; each call
consumes and disposes its result reader. The SDK control-command API has no
cancellation-token argument, so CreateAsync checks cancellation before each command
but cannot cancel a command already in progress.

Both methods also save related entities marked with `Reference`:

```csharp
var order = new Order
{
    Id = 100,
    Customer = new Customer { Id = 42 }
};
admin.Create(order); // Ingests Customer first; order.CustomerId becomes 42.
// Alternatively: await admin.CreateAsync(order, cancellationToken);

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
- Nested references are ingested before their owners. A shared object instance is
  ingested once per call; distinct instances with equal keys are separate inserts.
- The complete graph, mappings and values are validated before sending any command.
  Cycles and invalid relationships fail locally. Each entity uses its mapped database.
- Commands execute sequentially without a graph-wide transaction. If ingestion
  fails or cancellation stops the remaining commands, earlier inserts remain in
  Kusto. Changed FK properties in memory are restored to their original values.
  Retrying the entire graph can therefore duplicate previously ingested rows.
- `Key` and `ForeignKey` describe relationships in the model; this API does not
  create database constraints, generate keys, load navigations or save collections.

Microsoft documents [inline ingestion](https://learn.microsoft.com/en-us/kusto/management/data-ingestion/ingest-inline)
for exploration and prototyping, not production or high-volume ingestion. Use
the Kusto ingestion SDK for those workloads.

Run local tests without a cluster:

```powershell
dotnet test tests/Akeldov.Data.Kusto.Tests/Akeldov.Data.Kusto.Tests.csproj
```

Tests cover query construction, SDK parameter passing, result mapping,
resource ownership, cancellation, and syntax/type validation with Microsoft's
`Kusto.Language` analyzer. They do not require cluster credentials.

Run integration tests against the Docker-hosted Kusto Emulator:

```powershell
dotnet test tests/Akeldov.Data.Kusto.IntegrationTests/Akeldov.Data.Kusto.IntegrationTests.csproj
```

See the [integration test setup and coverage](../../tests/Akeldov.Data.Kusto.IntegrationTests/README.md).
