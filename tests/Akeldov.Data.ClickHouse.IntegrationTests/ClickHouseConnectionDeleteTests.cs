using System.Data;
using ClickHouse.Driver.ADO;
using NUnit.Framework;

namespace Akeldov.Data.ClickHouse.IntegrationTests;

public partial class ClickHouseConnectionSelectTests
{
    [Test]
    public async Task Delete_UsesAllKeysAndWaitsUntilAllMatchesAreDeleted(
        [Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var connection = new ClickHouseConnection(connectionString);
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        connection.Create(new UpdateRow { Id = id, TenantId = 10, Name = "delete first" });
        connection.Create(new UpdateRow { Id = id, TenantId = 10, Name = "delete duplicate" });
        connection.Create(new UpdateRow { Id = id, TenantId = 20, Name = "other tenant" });
        connection.Create(new UpdateRow { Id = otherId, TenantId = 10, Name = "other row" });
        if (initiallyOpen) await connection.OpenAsync();
        var entity = new DeleteModel { Id = id, TenantId = 10 };

        if (asynchronous) await connection.DeleteAsync(entity);
        else connection.Delete(entity);

        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        Assert.That(connection.Select<UpdateRow>(row => row.Id == id && row.TenantId == 10), Is.Empty);
        Assert.That(connection.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("other tenant"));
        Assert.That(connection.Select<UpdateRow>(row => row.Id == otherId).Single().Name, Is.EqualTo("other row"));
        Assert.That(entity.Id, Is.EqualTo(id));
        Assert.That(entity.TenantId, Is.EqualTo(10));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_NoMatchingKey_LeavesRowsUnchanged(bool asynchronous)
    {
        using var connection = new ClickHouseConnection(connectionString);
        var id = Guid.NewGuid();
        connection.Create(new UpdateRow { Id = id, TenantId = 10, Name = "unchanged" });
        var missing = new DeleteModel { Id = id, TenantId = 20 };
        if (asynchronous) await connection.DeleteAsync(missing);
        else connection.Delete(missing);
        Assert.That(connection.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("unchanged"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_DoesNotTraverseReferences(bool asynchronous)
    {
        using var connection = new ClickHouseConnection(connectionString);
        var customer = new Customer { Id = Guid.NewGuid() };
        var order = new DeleteOrder { Id = Guid.NewGuid(), Customer = customer };
        connection.Create(order);
        if (asynchronous) await connection.DeleteAsync(order);
        else connection.Delete(order);
        Assert.That(connection.Select<DeleteOrder>(row => row.Id == order.Id), Is.Empty);
        Assert.That(connection.Select<Customer>(row => row.Id == customer.Id), Has.Count.EqualTo(1));
        Assert.That(order.CustomerId, Is.EqualTo(customer.Id));
        Assert.That(order.Customer, Is.SameAs(customer));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DeleteAsync_PreCanceled_PreservesConnectionAndRow(bool initiallyOpen)
    {
        using var connection = new ClickHouseConnection(connectionString);
        var id = Guid.NewGuid();
        connection.Create(new UpdateRow { Id = id, TenantId = 10, Name = "unchanged" });
        if (initiallyOpen) connection.Open();
        Assert.That(async () => await connection.DeleteAsync(new DeleteModel { Id = id, TenantId = 10 }, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        Assert.That(connection.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("unchanged"));
    }

    [Test]
    public void Delete_ServerError_PreservesConnectionState([Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var connection = new ClickHouseConnection(connectionString);
        if (initiallyOpen) connection.Open();
        Assert.That(async () =>
        {
            if (asynchronous) await connection.DeleteAsync(new MissingDeleteRow());
            else connection.Delete(new MissingDeleteRow());
        }, Throws.Exception);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        Assert.That(connection.Select<User>(), Has.Count.EqualTo(5));
    }

    [Table("analytics", "update_rows")]
    public class DeleteModel
    {
        [Key, Column("id")] public Guid Id { get; set; }
        [Key, Column("tenant_id")] public int TenantId { get; set; }
    }

    [Table("analytics", "delete_orders")]
    public class DeleteOrder
    {
        [Key, Column("id")] public Guid Id { get; set; }
        [Column("customer_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))] public Guid CustomerId { get; set; }
        [Reference(nameof(CustomerId))] public Customer? Customer { get; set; }
    }

    [Table("analytics", "missing_delete_rows")]
    public class MissingDeleteRow { [Key, Column("id")] public Guid Id { get; set; } }
}
