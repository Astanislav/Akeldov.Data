# Akeldov.Data.MongoDb

Reads mapped models with the official `MongoDB.Driver` SDK. Add a project
reference to `Akeldov.Data.MongoDb.csproj` and import `Akeldov.Data.MongoDb`.

```csharp
using Akeldov.Data;
using Akeldov.Data.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;

using var client = new MongoClient("mongodb://localhost:27017");
var all = client.Select<User>();
var minimum = 10;
var matching = client.Select<User>(row => row.Score >= minimum && row.Active);
var asyncRows = await client.SelectAsync<User>(row => row.Name == "Alice", cancellationToken);

[Table("analytics", "users")]
public class User
{
    [Column("_id")] public ObjectId Id { get; set; }
    [Column("name")] public string? Name { get; set; }
    [Column("score")] public int? Score { get; set; }
    [Column("active")] public bool Active { get; set; }
}
```

`cancellationToken` is a caller-supplied `CancellationToken`. `Table.Schema`
specifies the database; `Table.Name` specifies the collection. Only properties
marked with `Column` are projected and populated. Field names are case-sensitive.
The `_id` field is excluded unless a property explicitly has `[Column("_id")]`.
It may be an `ObjectId`, `Guid`, string, number, or another driver-supported type.
It does not have to be a GUID. A missing field keeps the property's initialized
value; BSON null sets nullable properties to null and throws for non-nullable
value types. Extra fields are ignored. Dates deserialize as UTC `DateTime`.

GUID and nullable GUID properties use BSON UUID subtype 4 (`GuidRepresentation.Standard`)
for both filters and results, following the driver's
[GUID serialization guidance](https://www.mongodb.com/docs/drivers/csharp/current/serialization/guids/).
Legacy subtype 3 UUIDs require migration or explicit handling with the SDK.
The provider uses a local serializer and does not register global serializers,
class maps, or conventions. `Column` controls mapped field names even if the
same model also has MongoDB mapping attributes.

Predicates are translated by the driver's expression filter translator, using
the mapped field names. Comparisons, boolean operations, null checks, captured
values, field comparisons, and other expressions supported by the driver are
available. Unsupported expressions and unmapped properties throw before the
client accesses the database. Values are serialized as BSON data. MongoDB null
equality matches both null and missing fields. MongoDB query semantics apply;
see the driver's [query filter documentation](https://www.mongodb.com/docs/drivers/csharp/current/crud/query/query-filter/).

Column names must be top-level field names without dots, a leading `$`, or null
characters. Nested member queries can use a mapped object property and the
driver's serializers for that object's members. Use the SDK directly for
writes, aggregation, sorting, paging, and legacy UUID handling.

The caller owns and disposes the client. Each select disposes its cursor, also
on mapping errors or cancellation. The async API passes cancellation to both
the initial query and subsequent cursor batches, and checks it between documents.

Run local tests without MongoDB:

```powershell
dotnet test tests/Akeldov.Data.MongoDb.Tests/Akeldov.Data.MongoDb.Tests.csproj
```

Run integration tests with Docker using Linux containers:

```powershell
dotnet test tests/Akeldov.Data.MongoDb.IntegrationTests/Akeldov.Data.MongoDb.IntegrationTests.csproj
```

Alternatively, set `MONGODB_CONNECTION_STRING` to a test server. Integration
tests replace data in the `users`, `guid_keys`, `string_keys`, `number_keys`, and
`case_fields` collections of the dedicated `akeldov_data_integration` database.
They cover sync/async execution, mapped projection, null and missing fields,
field comparisons, case-sensitive names, cancellation, and several key types.
