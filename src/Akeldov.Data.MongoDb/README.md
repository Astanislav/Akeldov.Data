# Akeldov.Data.MongoDb

Reads and inserts mapped models with the official `MongoDB.Driver` SDK. Add a project
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

client.Create<User>(new User { Id = ObjectId.GenerateNewId(), Name = "Alice" });
await client.CreateAsync(new User { Id = ObjectId.GenerateNewId(), Name = "Bob" }, cancellationToken);

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
bulk writes, updates, deletes, aggregation, sorting, paging, and legacy UUID handling.

`Create<T>(entity)` returns `void`; `CreateAsync<T>` returns `Task`. Both insert
a document and its `Reference` entities through the same local serializer, including only `Column` properties
with public getters and setters. Null values become BSON null. A mapped `_id` is
sent exactly as supplied, including default values; assign it before calling
Create. Without a mapped `_id`, MongoDB generates one. Generated IDs are not
written back to the entity. CreateAsync passes cancellation to `InsertOneAsync`.

Both methods also save related entities using `Key`, `ForeignKey` and `Reference`:

```csharp
var order = new Order
{
    Id = ObjectId.GenerateNewId(),
    Customer = new Customer { Id = ObjectId.GenerateNewId() }
};
client.Create(order); // Inserts Customer first; copies its Id to order.CustomerId.
// Alternatively: await client.CreateAsync(order, cancellationToken);

[Table("analytics", "customers")]
public class Customer
{
    [Key, Column("_id")] public ObjectId Id { get; set; }
}

[Table("analytics", "orders")]
public class Order
{
    [Key, Column("_id")] public ObjectId Id { get; set; }
    [Column("customer_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))]
    public ObjectId CustomerId { get; set; }
    [Reference(nameof(CustomerId))] public Customer? Customer { get; set; }
}
```

- Supply referenced keys before calling either method. A referenced property must
  have `Key` and `Column` and contain a non-null value. It can map to `_id` or another
  field. The local FK must have `Column` and `ForeignKey` and a matching CLR type
  (nullable versions are allowed). Navigation properties must not have `Column`.
- Non-null references are treated as new documents in their mapped database and
  collection; their keys replace the supplied foreign keys. To use an existing
  document, leave the navigation null and supply its FK.
- Nested references are inserted before their owners. A shared object instance is
  inserted once per call. Distinct instances with equal keys remain separate
  inserts and may fail on MongoDB's unique indexes, including `_id`.
- The complete graph is validated and serialized to BSON before accessing the
  database. Cycles, invalid relationships, and serialization errors fail locally.
  Navigation properties are not embedded; only the mapped FK fields are stored.
- Inserts execute sequentially without a graph-wide transaction. On failure or
  cancellation, earlier documents remain in MongoDB; changed FK properties in
  memory are restored to their original values. Retrying the graph may encounter
  duplicate keys or create duplicate documents when IDs were generated by the driver.
- `CreateAsync` awaits each insert, forwards cancellation to the driver, and checks
  cancellation before the next insert. The caller retains ownership of the client.
- These attributes describe model relationships; this API does not create database
  constraints, generate referenced keys, load navigations or save collections.
  Ordinary mapped object properties still use the existing BSON serialization.

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
Create tests also insert documents into its `created_rows`, `created_regions`,
`created_customers`, and `created_orders` collections.
They cover sync/async execution, mapped projection, null and missing fields,
field comparisons, case-sensitive names, cancellation, and several key types.
Graph tests cover nested/shared references and BSON key round trips, plus retained
dependency documents and restored in-memory FKs after a duplicate-key failure.
