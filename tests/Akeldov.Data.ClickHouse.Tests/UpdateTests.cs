using System.Data;
using System.Net;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.ADO.Parameters;
using NUnit.Framework;

namespace Akeldov.Data.ClickHouse.Tests;

[TestFixture]
public class UpdateTests
{
    [Test]
    public void CreateCommand_UsesClickHouseMutationParametersAndCompositeKeys()
    {
        using var connection = new ClickHouseConnection();
        var row = new Row
        {
            Id = 12, TenantId = Guid.NewGuid(), Name = "O'Brien'); DROP TABLE rows;--",
            State = State.Active, Created = DateTime.UnixEpoch.AddTicks(1234567), Amount = 12.3456m
        };
        using var command = ClickHouseUpdateExecutor.CreateCommand(connection, row);
        Assert.Multiple(() =>
        {
            Assert.That(command.CommandText, Is.EqualTo(
                "ALTER TABLE `odd\\`schema`.`rows\\`table` UPDATE `na\\`me` = @p0, `score` = NULL, `state` = @p1, `created` = @p2, `amount` = @p3 WHERE `id` = @p4 AND `tenant_id` = @p5 SETTINGS mutations_sync = 1"));
            Assert.That(command.CommandText, Does.Not.Contain(row.Name));
            Assert.That(command.Parameters.Cast<ClickHouseDbParameter>().Select(parameter => parameter.Value),
                Is.EqualTo(new object[] { row.Name, (short)1, row.Created, row.Amount, 12, row.TenantId }));
            Assert.That(command.Parameters.Cast<ClickHouseDbParameter>().Select(parameter => parameter.ClickHouseType),
                Is.EqualTo(new[] { "String", "Int16", "DateTime64(7, 'UTC')", "Decimal(38, 4)", "Int32", "UUID" }));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void CreateCommand_WritesForeignKeyWithoutTraversingReference()
    {
        using var connection = new ClickHouseConnection();
        var row = new Order { Id = 100, CustomerId = 43, Customer = new Customer { Id = 999 } };

        using var command = ClickHouseUpdateExecutor.CreateCommand(connection, row);

        Assert.That(command.CommandText, Is.EqualTo(
            "ALTER TABLE `analytics`.`orders` UPDATE `customer_id` = @p0 WHERE `id` = @p1 SETTINGS mutations_sync = 1"));
        Assert.That(command.Parameters.Cast<ClickHouseDbParameter>().Select(parameter => parameter.Value),
            Is.EqualTo(new object[] { 43, 100 }));
        Assert.That(row.Customer.Id, Is.EqualTo(999));
    }

    [Test]
    public async Task Update_UsesNativeParametersAndPreservesConnection(
        [Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var handler = new ClickHouseConnectionExtensionsTests.FakeServer { ResponseData = [] };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        if (initiallyOpen) connection.Open();
        var row = new Row { Id = 12, TenantId = Guid.NewGuid(), Name = "O'Brien'); DROP TABLE rows;--" };

        if (asynchronous) await connection.UpdateAsync(row);
        else connection.Update(row);

        Assert.Multiple(() =>
        {
            Assert.That(handler.Sql, Does.Contain("ALTER TABLE `odd\\`schema`.`rows\\`table` UPDATE"));
            Assert.That(handler.Sql, Does.Contain("{p0:String}"));
            Assert.That(handler.Sql, Does.Contain("SETTINGS mutations_sync = 1"));
            Assert.That(handler.Sql, Does.Not.Contain(row.Name));
            Assert.That(WebUtility.UrlDecode(handler.Uri!.Query), Does.Contain("DROP TABLE"));
            Assert.That(handler.ResponseStream!.Disposed, Is.True);
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Update_InvalidArgumentsOrMapping_DoNotSendRequest(bool asynchronous)
    {
        using var handler = new ClickHouseConnectionExtensionsTests.FakeServer { ResponseData = [] };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Update<Row>(connection, null!, asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Update(null!, new Row(), asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Update(connection, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("at least one Key"));
            Assert.That(async () => await Update(connection, new UnmappedKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
            Assert.That(async () => await Update(connection, new NullKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Update(connection, new KeyOnly(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("non-key Column"));
            Assert.That(async () => await Update(connection, new Unsupported(), asynchronous),
                Throws.TypeOf<NotSupportedException>());
            Assert.That(handler.Requests, Is.Zero);
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void UpdateAsync_PreCanceled_DoesNotSendRequest()
    {
        using var handler = new ClickHouseConnectionExtensionsTests.FakeServer { ResponseData = [] };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        Assert.That(async () => await connection.UpdateAsync(new Row(), new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(handler.Requests, Is.Zero);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void Update_ServerError_PreservesConnectionState([Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var handler = new ClickHouseConnectionExtensionsTests.FakeServer { Fail = true };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        if (initiallyOpen) connection.Open();
        Assert.That(async () => await Update(connection, new Row(), asynchronous), Throws.Exception);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    private static Task Update<T>(ClickHouseConnection connection, T row, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return connection.UpdateAsync(row);
        connection.Update(row);
        return Task.CompletedTask;
    }

    private static ClickHouseConnection CreateConnection(HttpClient http)
        => new("Host=localhost;Port=8123;Database=analytics;Username=default;Compression=false", http);

    [Table("odd`schema", "rows`table")]
    public class Row
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("tenant_id")] public Guid TenantId { get; set; }
        [Column("na`me")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("state")] public State State { get; set; }
        [Column("created")] public DateTime Created { get; set; }
        [Column("amount")] public decimal Amount { get; set; }
    }

    public enum State : short { Active = 1 }
    [Table("analytics", "rows")]
    public class MissingKey { [Column("value")] public int Value { get; set; } }
    [Table("analytics", "rows")]
    public class UnmappedKey
    {
        [Key] public int Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }
    [Table("analytics", "rows")]
    public class NullKey
    {
        [Key, Column("id")] public int? Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }
    [Table("analytics", "rows")]
    public class KeyOnly { [Key, Column("id")] public int Id { get; set; } }
    [Table("analytics", "rows")]
    public class Unsupported
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("duration")] public TimeSpan Duration { get; set; }
    }
    [Table("analytics", "customers")]
    public class Customer { [Key, Column("id")] public int Id { get; set; } }
    [Table("analytics", "orders")]
    public class Order
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("customer_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))] public int CustomerId { get; set; }
        [Reference(nameof(CustomerId))] public Customer? Customer { get; set; }
    }
}
