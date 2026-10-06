# Akeldov.Data.Kusto

Reads mapped models from Azure Data Explorer / Kusto using the official
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

Run local tests without a cluster:

```powershell
dotnet test tests/Akeldov.Data.Kusto.Tests/Akeldov.Data.Kusto.Tests.csproj
```

Tests cover query construction, SDK parameter passing, result mapping,
resource ownership, cancellation, and syntax/type validation with Microsoft's
`Kusto.Language` analyzer. They do not require cluster credentials.
