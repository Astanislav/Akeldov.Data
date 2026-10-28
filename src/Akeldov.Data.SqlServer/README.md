# SQL Server

`Create` and `CreateAsync` insert mapped rows and recursively save entities marked with `Reference`.
The referenced entity is inserted first; its `Key` value is then copied into the
local property named by `Reference`, which must have `Column` and `ForeignKey`.

```csharp
using Akeldov.Data;
using Akeldov.Data.SqlServer;
using Microsoft.Data.SqlClient;

using var connection = new SqlConnection(connectionString);
var order = new Order
{
    Id = 100,
    Customer = new Customer { Id = 42, Name = "Alice" }
};
connection.Create(order); // Inserts Customer, then Order; order.CustomerId becomes 42.
// Alternatively: await connection.CreateAsync(order, cancellationToken);

[Table("dbo", "Customers")]
public class Customer
{
    [Key, Column("Id")]
    public int Id { get; set; }

    [Column("Name")]
    public string? Name { get; set; }
}

[Table("dbo", "Orders")]
public class Order
{
    [Key, Column("Id")]
    public int Id { get; set; }

    [Column("CustomerId"), ForeignKey(typeof(Customer), nameof(Customer.Id))]
    public int CustomerId { get; set; }

    [Reference(nameof(CustomerId))]
    public Customer? Customer { get; set; }
}
```

- Supply key values before calling either method. `Key` does not generate values or
  retrieve database-generated identities. Referenced keys must be non-null.
- A non-null reference is treated as a new row, and its key replaces the supplied
  foreign key. To reference an existing row, supply only the foreign key and leave
  the navigation null. Navigation properties themselves must not have `Column`.
- The same object instance is inserted once per call, even when several references
  point to it. Different instances with the same key remain separate inserts.
- References can be nested. Cycles, invalid relationship metadata and unsupported
  mapped values fail before the connection is opened or any rows are inserted.
- Multi-row graphs use one transaction. On failure or cancellation, rollback is
  attempted and changed foreign key properties in memory are restored. A connection
  opened by either method is closed afterwards; an already open connection stays open.
- `CreateAsync` returns `Task` and accepts an optional `CancellationToken`. It uses
  asynchronous connection, transaction, and command APIs, awaiting each dependency
  before inserting its owner. Cancellation is checked before each insert and commit
  and passed to the driver. Rollback uses an uncanceled token so cancellation does
  not prevent cleanup; a rollback error does not replace the original operation error.
- A graph containing only one row does not start an explicit transaction.
- `Reference` describes a single entity reached through a local foreign key, not
  a collection of children.

## Update

`Update` and `UpdateAsync` update an existing row using its caller-supplied keys:

```csharp
connection.Update(new Customer { Id = 42, Name = "Updated name" });
await connection.UpdateAsync(new Customer { Id = 42, Name = null }, cancellationToken);
```

- `Update` returns `void`; `UpdateAsync` returns `Task` and accepts an optional
  `CancellationToken`.
- Every `Key` must also have `Column` and a non-null value. Composite keys are
  supported: all key columns are combined with `AND` in `WHERE`. Keys are never
  written in `SET`; changing a primary key is outside this API.
- Every non-key `Column` is updated, including null values. Unmapped properties
  are ignored. This is a full mapped-row update, without change tracking or patch semantics.
- `Reference` entities are not inserted or updated. Local FK values are sent as
  supplied; a navigation's key does not override them. SQL Server enforces existing
  database constraints.
- Identifiers are quoted and values are parameterized. Missing/null keys, invalid
  mappings and models with no non-key columns fail before opening the connection.
- No matching row is not treated as an error; the method does not insert a row or
  return the affected-row count. Generated values are not returned to the entity.
- Each call executes one statement without starting an explicit transaction.
  Connections opened by the method are closed on completion or failure; already
  open connections remain open. Commands are disposed. The async method passes
  cancellation to connection opening and execution.

Run local tests without a server:

```powershell
dotnet test tests/Akeldov.Data.SqlServer.Tests/Akeldov.Data.SqlServer.Tests.csproj
```

Run integration tests with Docker using Linux containers:

```powershell
dotnet test tests/Akeldov.Data.SqlServer.IntegrationTests/Akeldov.Data.SqlServer.IntegrationTests.csproj
```
