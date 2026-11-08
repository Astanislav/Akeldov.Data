# Akeldov.Data.MongoDb

Reads, inserts, updates, and deletes mapped models with the official `MongoDB.Driver` SDK. Add a project
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

var userId = ObjectId.GenerateNewId();
client.Create<User>(new User { Id = userId, Name = "Alice" });
await client.CreateAsync(new User { Id = ObjectId.GenerateNewId(), Name = "Bob" }, cancellationToken);
client.Update(new User { Id = userId, Name = "Updated", Active = true });
await client.UpdateAsync(new User { Id = userId, Name = null }, cancellationToken);

[Table("analytics", "users")]
public class User
{
    [Key, Column("_id")] public ObjectId Id { get; set; }
    [Column("name")] public string? Name { get; set; }
    [Column("score")] public int? Score { get; set; }
    [Column("active")] public bool Active { get; set; }
}
```

`userId` and `cancellationToken` are caller-supplied values. `Table.Schema`
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
bulk writes, custom update/delete operations, aggregation, sorting, paging, and legacy UUID handling.

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

## Update

`Update` and `UpdateAsync` update every document matching the caller-supplied keys
using `UpdateMany` with a `$set` update document.

- Every `Key` must also have `Column` and a non-null value. Composite keys are
  supported: all key fields are combined in one equality filter. Key fields are
  never included in `$set`; changing a key is outside this API.
- Every non-key `Column` is written, including BSON null values. This is a full
  mapped-model update without change tracking or patch semantics. Unmapped model
  properties are ignored, while existing document fields absent from the model
  remain unchanged.
- `Reference` entities are not inserted or updated. Mapped local foreign keys are
  written exactly as supplied; navigation keys do not replace them.
- The same operation-local serializers as Create and Select produce both the key
  filter and update values. UUIDs therefore use subtype 4, dates use UTC, mapped
  names are honored, and invalid top-level field names fail before database access.
- `UpdateMany` updates every matching document. `IsUpsert` is explicitly false,
  so no match is a no-op and never inserts a document. The methods do not return
  matched or modified counts.
- Each individual document update is atomic, but an update matching multiple
  documents is not atomic as a whole and does not start a transaction. If a later
  document fails, earlier successful document updates remain.
- MongoDB does not allow changing `_id`; map it as a `Key` when it identifies the
  document. Other server-side schema, validation, shard-key, and immutable-field
  restrictions still apply.
- The caller owns the client. `UpdateAsync` passes cancellation to the driver and
  checks it before accessing the database.

See MongoDB's [`updateMany` documentation](https://www.mongodb.com/docs/manual/reference/method/db.collection.updateMany/)
for multi-document atomicity, sharding behavior, and server restrictions.

## Delete

`Delete` and `DeleteAsync` delete every document matching the supplied entity's keys:

```csharp
client.Delete(new User { Id = userId });
// Alternatively:
await client.DeleteAsync(new User { Id = userId }, cancellationToken);
```

- `Delete` returns `void`; `DeleteAsync` returns `Task`. They call `DeleteMany`
  and `DeleteManyAsync` respectively. No match is a no-op; deleted counts are not returned.
- Every `Key` must have `Column` and a non-null value. All keys participate in the
  filter, including composite keys. Key-only models work. Default non-null keys,
  such as `0`, `Guid.Empty` and `ObjectId.Empty`, are sent as supplied.
- Keys use the existing operation-local serializers: `Column` controls field
  names, GUIDs use standard UUID subtype 4, and dates use UTC. Explicit `$eq`
  comparisons keep BSON document and regex keys from being interpreted as operators.
  MongoDB equality semantics apply.
- Invalid mappings, field names, null keys (including serialized BSON null), and
  key serialization failures are rejected before database access. Non-key values
  are not read or serialized, and `Reference` entities are not traversed.
- The supplied entity remains unchanged. No transaction is started; deletion of
  multiple documents is not atomic as a whole. The caller owns the client.
- `DeleteAsync` checks cancellation before database access, passes the token to
  the driver, and awaits completion. Cancellation does not roll back documents
  already deleted by the server.

See the driver's [delete documentation](https://www.mongodb.com/docs/drivers/csharp/current/crud/delete/).

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
Update tests use unique keys in `update_rows` and cover composite-key matching,
multiple matching documents, null values, preservation of unmapped fields,
no-upsert behavior, and cancellation.
Delete tests use unique keys in `update_rows`, `created_orders`, `created_customers`,
`created_regions`, and `delete_string_keys`. They cover composite keys, multiple
matches, key-only models, ObjectId/GUID/string keys, preserved references,
no-match behavior and pre-cancellation.
