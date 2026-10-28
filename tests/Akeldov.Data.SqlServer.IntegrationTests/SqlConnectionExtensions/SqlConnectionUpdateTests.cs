using System.Data;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.SqlServer.IntegrationTests;

public partial class SqlConnectionSelectTests
{
    [Test]
    public async Task Update_UsesAllKeysAndRoundTripsMappedValues(
        [Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Create(new UpdateRow { Id = 1, TenantId = 10, Name = "before", Score = 5 });
        connection.Create(new UpdateRow { Id = 1, TenantId = 20, Name = "other tenant" });
        connection.Create(new UpdateRow { Id = 2, TenantId = 10, Name = "other row" });
        if (initiallyOpen) await connection.OpenAsync();
        var row = new UpdateRow
        {
            Id = 1, TenantId = 10, Name = "O'Brien'); DROP TABLE rows;-- Ольга", Score = null,
            Created = DateTime.UnixEpoch.AddTicks(1234567), Amount = 12.3456m,
            ExternalId = Guid.NewGuid(), Payload = [0, 1, 255]
        };

        if (asynchronous) await connection.UpdateAsync(row);
        else connection.Update(row);

        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        var actual = connection.Select<UpdateRow>(item => item.Id == 1 && item.TenantId == 10).Single();
        Assert.Multiple(() =>
        {
            Assert.That(actual.Name, Is.EqualTo(row.Name));
            Assert.That(actual.Score, Is.Null);
            Assert.That(actual.Created, Is.EqualTo(row.Created));
            Assert.That(actual.Amount, Is.EqualTo(row.Amount));
            Assert.That(actual.ExternalId, Is.EqualTo(row.ExternalId));
            Assert.That(actual.Payload, Is.EqualTo(row.Payload));
        });
        Assert.That(connection.Select<UpdateRow>(), Has.Count.EqualTo(3));
        Assert.That(connection.Select<UpdateRow>(item => item.TenantId == 20).Single().Name, Is.EqualTo("other tenant"));
        Assert.That(connection.Select<UpdateRow>(item => item.Id == 2).Single().Name, Is.EqualTo("other row"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Update_NoMatchingKey_DoesNotInsertOrChangeRows(bool asynchronous)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Create(new UpdateRow { Id = 1, TenantId = 10, Name = "unchanged" });
        var missing = new UpdateRow { Id = 1, TenantId = 20, Name = "missing tenant" };
        if (asynchronous) await connection.UpdateAsync(missing);
        else connection.Update(missing);
        var actual = connection.Select<UpdateRow>().Single();
        Assert.That(actual.TenantId, Is.EqualTo(10));
        Assert.That(actual.Name, Is.EqualTo("unchanged"));
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Update_WritesForeignKeyWithoutSavingReference(bool asynchronous)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Create(new Order { Id = 100, Customer = new Customer { Id = 42 } });
        connection.Create(new Customer { Id = 43 });
        var row = new Order { Id = 100, CustomerId = 43, Customer = new Customer { Id = 999 } };
        if (asynchronous) await connection.UpdateAsync(row);
        else connection.Update(row);
        Assert.That(connection.Select<Order>().Single().CustomerId, Is.EqualTo(43));
        Assert.That(connection.Select<Customer>().Select(customer => customer.Id), Is.EquivalentTo(new[] { 42, 43 }));
        Assert.That(row.CustomerId, Is.EqualTo(43));
        Assert.That(row.Customer.Id, Is.EqualTo(999));
    }

    [Test]
    public void Update_ConstraintFailure_PreservesConnectionStateAndExistingRow(
        [Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Create(new Order { Id = 100, Customer = new Customer { Id = 42 } });
        if (initiallyOpen) connection.Open();
        var invalid = new Order { Id = 100, CustomerId = 999 };
        Assert.That(async () =>
        {
            if (asynchronous) await connection.UpdateAsync(invalid);
            else connection.Update(invalid);
        }, Throws.TypeOf<SqlException>().With.Property(nameof(SqlException.Number)).EqualTo(547));
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        Assert.That(connection.Select<Order>().Single().CustomerId, Is.EqualTo(42));
        Assert.That(invalid.CustomerId, Is.EqualTo(999));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UpdateAsync_PreCanceled_PreservesConnectionStateAndExistingRow(bool initiallyOpen)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Create(new UpdateRow { Id = 1, TenantId = 10, Name = "unchanged" });
        if (initiallyOpen) connection.Open();
        Assert.That(async () => await connection.UpdateAsync(
            new UpdateRow { Id = 1, TenantId = 10, Name = "canceled" }, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        Assert.That(connection.Select<UpdateRow>().Single().Name, Is.EqualTo("unchanged"));
    }

    [Table("integration", "update_rows")]
    public class UpdateRow
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("tenant_id")] public int TenantId { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("created")] public DateTime Created { get; set; } = DateTime.UnixEpoch;
        [Column("amount")] public decimal Amount { get; set; }
        [Column("external_id")] public Guid ExternalId { get; set; }
        [Column("payload")] public byte[]? Payload { get; set; }
    }
}
