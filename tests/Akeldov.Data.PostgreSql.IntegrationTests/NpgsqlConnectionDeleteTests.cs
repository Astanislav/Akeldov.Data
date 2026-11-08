using System.Data;
using Npgsql;
using NUnit.Framework;

namespace Akeldov.Data.PostgreSql.IntegrationTests;

public partial class NpgsqlConnectionSelectTests
{
    [Test]
    public async Task Delete_UsesAllKeysAndPreservesConnectionState(
        [Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Create(new UpdateRow { Id = 1, TenantId = 10, Name = "delete" });
        connection.Create(new UpdateRow { Id = 1, TenantId = 20, Name = "other tenant" });
        connection.Create(new UpdateRow { Id = 2, TenantId = 10, Name = "other row" });
        if (initiallyOpen) await connection.OpenAsync();
        var row = new UpdateRow { Id = 1, TenantId = 10, Name = "ignored" };

        if (asynchronous) await connection.DeleteAsync(row);
        else connection.Delete(row);

        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        Assert.That(connection.Select<UpdateRow>().Select(item => (item.Id, item.TenantId, item.Name)),
            Is.EquivalentTo(new[] { (1, 20, "other tenant"), (2, 10, "other row") }));
        Assert.That(row.Name, Is.EqualTo("ignored"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_NoMatchingKey_LeavesRowsUnchanged(bool asynchronous)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Create(new UpdateRow { Id = 1, TenantId = 10, Name = "unchanged" });
        var missing = new UpdateRow { Id = 1, TenantId = 20 };
        if (asynchronous) await connection.DeleteAsync(missing);
        else connection.Delete(missing);
        var actual = connection.Select<UpdateRow>().Single();
        Assert.That(actual.TenantId, Is.EqualTo(10));
        Assert.That(actual.Name, Is.EqualTo("unchanged"));
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_DoesNotTraverseReferences(bool asynchronous)
    {
        using var connection = new NpgsqlConnection(connectionString);
        var order = new Order { Id = 100, Customer = new Customer { Id = 42 } };
        connection.Create(order);

        if (asynchronous) await connection.DeleteAsync(order);
        else connection.Delete(order);

        Assert.That(connection.Select<Order>(), Is.Empty);
        Assert.That(connection.Select<Customer>().Single().Id, Is.EqualTo(42));
        Assert.That(order.CustomerId, Is.EqualTo(42));
        Assert.That(order.Customer!.Id, Is.EqualTo(42));
    }

    [Test]
    public void Delete_ConstraintFailure_PreservesConnectionStateAndRows(
        [Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Create(new Order { Id = 100, Customer = new Customer { Id = 42 } });
        if (initiallyOpen) connection.Open();
        Assert.That(async () =>
        {
            if (asynchronous) await connection.DeleteAsync(new Customer { Id = 42 });
            else connection.Delete(new Customer { Id = 42 });
        }, Throws.TypeOf<PostgresException>()
            .With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.ForeignKeyViolation));
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        Assert.That(connection.Select<Customer>().Single().Id, Is.EqualTo(42));
        Assert.That(connection.Select<Order>().Single().CustomerId, Is.EqualTo(42));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DeleteAsync_PreCanceled_PreservesConnectionStateAndRow(bool initiallyOpen)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Create(new Customer { Id = 42 });
        if (initiallyOpen) connection.Open();
        Assert.That(async () => await connection.DeleteAsync(new Customer { Id = 42 }, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        Assert.That(connection.Select<Customer>().Single().Id, Is.EqualTo(42));
    }
}
