using System.Data;
using System.Net;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.ADO.Parameters;
using NUnit.Framework;

namespace Akeldov.Data.ClickHouse.Tests;

[TestFixture]
public class DeleteTests
{
    [Test]
    public void CreateCommand_UsesMutationWithTypedCompositeKeysAndEscapedIdentifiers()
    {
        using var connection = new ClickHouseConnection();
        var row = new Row { Id = 12, Tenant = "O'Brien'); DROP TABLE rows;--" };
        using var command = ClickHouseDeleteExecutor.CreateCommand(connection, row);
        Assert.Multiple(() =>
        {
            Assert.That(command.CommandText, Is.EqualTo(
                "ALTER TABLE `odd\\`schema`.`rows\\`table` DELETE WHERE `id` = @p0 AND `tenant\\`id` = @p1 SETTINGS mutations_sync = 1"));
            Assert.That(command.CommandText, Does.Not.Contain(row.Tenant));
            Assert.That(command.Parameters.Cast<ClickHouseDbParameter>().Select(parameter => parameter.Value),
                Is.EqualTo(new object[] { row.Id, row.Tenant }));
            Assert.That(command.Parameters.Cast<ClickHouseDbParameter>().Select(parameter => parameter.ClickHouseType),
                Is.EqualTo(new[] { "Int32", "String" }));
            Assert.That(command.Parameters.Cast<ClickHouseDbParameter>().Select(parameter => parameter.ParameterName),
                Is.EqualTo(new[] { "p0", "p1" }));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void CreateCommand_KeyOnlyModel_NormalizesEnumKey()
    {
        using var connection = new ClickHouseConnection();
        using var command = ClickHouseDeleteExecutor.CreateCommand(connection, new KeyOnly<Status> { Id = Status.Active });
        Assert.That(command.CommandText, Is.EqualTo(
            "ALTER TABLE `analytics`.`rows` DELETE WHERE `id` = @p0 SETTINGS mutations_sync = 1"));
        Assert.That(command.Parameters[0].Value, Is.EqualTo((short)1));
        Assert.That(((ClickHouseDbParameter)command.Parameters[0]).ClickHouseType, Is.EqualTo("Int16"));
    }

    [Test]
    public async Task Delete_UsesNativeParametersAndPreservesConnection(
        [Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var handler = new ClickHouseConnectionExtensionsTests.FakeServer { ResponseData = [] };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        if (initiallyOpen) connection.Open();
        var row = new Row { Id = 12, Tenant = "O'Brien'); DROP TABLE rows;--" };
        row.Child = row;
        await Delete(connection, row, asynchronous);
        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests, Is.EqualTo(1));
            Assert.That(handler.Sql, Does.Contain("ALTER TABLE `odd\\`schema`.`rows\\`table` DELETE WHERE"));
            Assert.That(handler.Sql, Does.Contain("`id` = {p0:Int32} AND `tenant\\`id` = {p1:String}"));
            Assert.That(handler.Sql, Does.Contain("SETTINGS mutations_sync = 1"));
            Assert.That(handler.Sql, Does.Not.Contain(row.Tenant));
            Assert.That(WebUtility.UrlDecode(handler.Uri!.Query), Does.Contain("DROP TABLE"));
            Assert.That(handler.ResponseStream!.Disposed, Is.True);
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
            Assert.That(row.Id, Is.EqualTo(12));
            Assert.That(row.Child, Is.SameAs(row));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Delete_InvalidArgumentsOrMapping_DoNotSendRequest(bool asynchronous)
    {
        using var handler = new ClickHouseConnectionExtensionsTests.FakeServer { ResponseData = [] };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Delete<Row>(connection, null!, asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Delete(null!, new Row(), asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Delete(connection, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("at least one Key"));
            Assert.That(async () => await Delete(connection, new UnmappedKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
            Assert.That(async () => await Delete(connection, new KeyOnly<int?>(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Delete(connection, new Row(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Delete(connection, new DuplicateColumn(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("mapped more than once"));
            Assert.That(async () => await Delete(connection, new KeyOnly<TimeSpan>(), asynchronous), Throws.TypeOf<NotSupportedException>());
            Assert.That(async () => await Delete(connection, new KeyOnly<double> { Id = double.NaN }, asynchronous), Throws.TypeOf<NotSupportedException>());
            Assert.That(handler.Requests, Is.Zero);
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DeleteAsync_PreCanceled_DoesNotSendRequest(bool initiallyOpen)
    {
        using var handler = new ClickHouseConnectionExtensionsTests.FakeServer { ResponseData = [] };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        if (initiallyOpen) connection.Open();
        Assert.That(async () => await connection.DeleteAsync(new KeyOnly<int>(), new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(handler.Requests, Is.Zero);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    [Test]
    public void Delete_ServerError_PreservesConnectionState([Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var handler = new ClickHouseConnectionExtensionsTests.FakeServer { Fail = true };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        if (initiallyOpen) connection.Open();
        Assert.That(async () => await Delete(connection, new KeyOnly<int>(), asynchronous), Throws.Exception);
        Assert.That(handler.Requests, Is.EqualTo(1));
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeleteAsync_CancellationDuringExecution_PreservesConnectionState(bool initiallyOpen)
    {
        using var handler = new PendingServer();
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        using var cancellation = new CancellationTokenSource();
        if (initiallyOpen) connection.Open();
        var delete = connection.DeleteAsync(new KeyOnly<int>(), cancellation.Token);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(delete.IsCompleted, Is.False);
        }
        finally { cancellation.Cancel(); }
        Assert.That(async () => await delete.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    private sealed class PendingServer : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static Task Delete<T>(ClickHouseConnection connection, T row, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return connection.DeleteAsync(row);
        connection.Delete(row);
        return Task.CompletedTask;
    }

    private static ClickHouseConnection CreateConnection(HttpClient http)
        => new("Host=localhost;Port=8123;Database=analytics;Username=default;Compression=false", http);

    [Table("odd`schema", "rows`table")]
    public class Row
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("tenant`id")] public string? Tenant { get; set; }
        [Column("ignored")] public TimeSpan Ignored { get => throw new InvalidOperationException(); set { } }
        [Reference(nameof(Id))] public Row? Child { get; set; }
    }
    public enum Status : short { Active = 1 }
    [Table("analytics", "rows")]
    public class KeyOnly<T> { [Key, Column("id")] public T? Id { get; set; } }
    [Table("analytics", "rows")]
    public class MissingKey { [Column("value")] public int Value { get; set; } }
    [Table("analytics", "rows")]
    public class UnmappedKey
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key] public int Tenant { get; set; }
    }
    [Table("analytics", "rows")]
    public class DuplicateColumn
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("id")] public int Other { get; set; }
    }
}
