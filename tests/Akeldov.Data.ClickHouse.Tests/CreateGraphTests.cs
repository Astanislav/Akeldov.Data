using System.Data;
using System.Net;
using ClickHouse.Driver.ADO;
using NUnit.Framework;

namespace Akeldov.Data.ClickHouse.Tests;

[TestFixture]
public class CreateGraphTests
{
    [Test]
    public async Task Create_InsertsNestedAndSharedReferencesBeforeOwner(
        [Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var server = new FakeServer();
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        if (initiallyOpen) connection.Open();
        var customer = new Customer { Id = 12, Region = new Region { Id = 7 } };
        var order = new Order { Id = 34, Buyer = customer, Recipient = customer };

        await Create(connection, order, asynchronous);

        Assert.Multiple(() =>
        {
            Assert.That(server.Commands, Has.Count.EqualTo(3));
            Assert.That(server.Commands[0].Sql, Does.Contain("INSERT INTO `analytics`.`regions` (`id`) VALUES ({p0:Int32})"));
            Assert.That(server.Commands[1].Sql, Does.Contain("INSERT INTO `analytics`.`customers` (`id`, `region_id`) VALUES ({p0:Int32}, {p1:Int32})"));
            Assert.That(server.Commands[2].Sql, Does.Contain("INSERT INTO `analytics`.`orders` (`id`, `buyer_id`, `recipient_id`) VALUES ({p0:Int32}, {p1:Int32}, {p2:Int32})"));
            Assert.That(Values(server.Commands[0].Uri), Is.EqualTo(new[] { "7" }));
            Assert.That(Values(server.Commands[1].Uri), Is.EqualTo(new[] { "12", "7" }));
            Assert.That(Values(server.Commands[2].Uri), Is.EqualTo(new[] { "34", "12", "12" }));
            Assert.That(customer.RegionId, Is.EqualTo(7));
            Assert.That(order.BuyerId, Is.EqualTo(12));
            Assert.That(order.RecipientId, Is.EqualTo(12));
            Assert.That(server.Streams.All(stream => stream.Disposed), Is.True);
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_NullReference_PreservesExistingForeignKey(bool asynchronous)
    {
        using var server = new FakeServer();
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        var row = new Customer { Id = 12, RegionId = 7 };
        await Create(connection, row, asynchronous);
        Assert.That(server.Commands, Has.Count.EqualTo(1));
        Assert.That(Values(server.Commands[0].Uri), Is.EqualTo(new[] { "12", "7" }));
        Assert.That(row.RegionId, Is.EqualTo(7));
    }

    [Test]
    public void Create_InsertFailure_StopsAndRestoresForeignKeys(
        [Values] bool asynchronous, [Values] bool initiallyOpen, [Values(1, 2, 3)] int failAt)
    {
        using var server = new FakeServer { FailAt = failAt };
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        if (initiallyOpen) connection.Open();
        var customer = new Customer { Id = 12, RegionId = 90, Region = new Region { Id = 7 } };
        var order = new Order { BuyerId = 91, RecipientId = 92, Buyer = customer, Recipient = customer };
        Assert.That(async () => await Create(connection, order, asynchronous),
            Throws.Exception.With.Message.Contains("Unknown table"));
        Assert.Multiple(() =>
        {
            Assert.That(server.Commands, Has.Count.EqualTo(failAt));
            Assert.That(server.Streams.All(stream => stream.Disposed), Is.True);
            Assert.That(customer.RegionId, Is.EqualTo(90));
            Assert.That(order.BuyerId, Is.EqualTo(91));
            Assert.That(order.RecipientId, Is.EqualTo(92));
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_InvalidGraph_ThrowsBeforeOpeningConnection(bool asynchronous)
    {
        using var server = new FakeServer();
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        var first = new RecursiveRow { Id = 1 };
        first.Next = new RecursiveRow { Id = 2, Next = first };
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Create(connection, first, asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Cyclic Reference"));
            Assert.That(async () => await Create(connection, new MissingForeignKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column and ForeignKey"));
            Assert.That(async () => await Create(connection, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Key and Column"));
            Assert.That(async () => await Create(connection, new WrongKeyType(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("incompatible foreign key and key types"));
            Assert.That(async () => await Create(connection, new DuplicateReference(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("already assigned"));
            Assert.That(async () => await Create(connection, new NullableReference { Child = new NullableKey() }, asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("null referenced key"));
            Assert.That(server.Commands, Is.Empty);
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_LateParameterFailure_RestoresKeysBeforeSending(bool asynchronous)
    {
        using var server = new FakeServer();
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        var row = new InvalidValue { RegionId = 99, Region = new Region { Id = 7 } };
        Assert.That(async () => await Create(connection, row, asynchronous), Throws.TypeOf<NotSupportedException>());
        Assert.That(row.RegionId, Is.EqualTo(99));
        Assert.That(server.Commands, Is.Empty);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_DistinctInstancesWithSameKey_AreNotMerged(bool asynchronous)
    {
        using var server = new FakeServer();
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        await Create(connection, new Order
        {
            Buyer = new Customer { Id = 12 }, Recipient = new Customer { Id = 12 }
        }, asynchronous);
        Assert.That(server.Commands, Has.Count.EqualTo(3));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_UsesEachDatabaseAndPreservesCaseDistinctColumns(bool asynchronous)
    {
        using var server = new FakeServer();
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        await Create(connection, new CaseRow { Id = 10, Region = new Region { Id = 7 } }, asynchronous);
        Assert.That(server.Commands, Has.Count.EqualTo(2));
        Assert.That(server.Commands[0].Sql, Does.Contain("INSERT INTO `analytics`.`regions`"));
        Assert.That(server.Commands[1].Sql, Does.Contain("INSERT INTO `reporting`.`case\\`rows` (`id`, `ID`) VALUES ({p0:Int32}, {p1:Int32})"));
        Assert.That(Values(server.Commands[1].Uri), Is.EqualTo(new[] { "10", "7" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CreateAsync_CancellationDuringRequest_StopsAndRestoresKeys(bool initiallyOpen)
    {
        using var cancellation = new CancellationTokenSource();
        using var server = new FakeServer
        {
            BeforeResponse = token =>
            {
                cancellation.Cancel();
                return Task.Delay(Timeout.Infinite, token);
            }
        };
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        if (initiallyOpen) connection.Open();
        var row = new Customer { RegionId = 99, Region = new Region { Id = 7 } };
        Assert.That(async () => await connection.CreateAsync(row, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(server.Commands, Has.Count.EqualTo(1));
        Assert.That(row.RegionId, Is.EqualTo(99));
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    [Test]
    public void CreateAsync_PreCanceled_DoesNotPrepareOrSendGraph()
    {
        using var server = new FakeServer();
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        var row = new Customer { RegionId = 99, Region = new Region { Id = 7 } };
        Assert.That(async () => await connection.CreateAsync(row, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(row.RegionId, Is.EqualTo(99));
        Assert.That(server.Commands, Is.Empty);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public async Task CreateAsync_AwaitsDependencyBeforeSendingOwner()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeServer { BeforeResponse = token => pending.Task.WaitAsync(token) };
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        var creation = connection.CreateAsync(new Customer { Id = 12, Region = new Region { Id = 7 } });
        try
        {
            Assert.That(server.Commands, Has.Count.EqualTo(1));
            Assert.That(creation.IsCompleted, Is.False);
        }
        finally { pending.SetResult(); }
        await creation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(server.Commands, Has.Count.EqualTo(2));
        Assert.That(server.Streams.All(stream => stream.Disposed), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_NullArguments_Throw(bool asynchronous)
    {
        using var server = new FakeServer();
        using var http = new HttpClient(server);
        using var connection = CreateConnection(http);
        Assert.That(async () => await Create<Customer>(connection, null!, asynchronous), Throws.ArgumentNullException);
        Assert.That(async () => await Create(null!, new Customer(), asynchronous), Throws.ArgumentNullException);
        Assert.That(server.Commands, Is.Empty);
    }

    private static Task Create<T>(ClickHouseConnection connection, T entity, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return connection.CreateAsync(entity);
        connection.Create(entity);
        return Task.CompletedTask;
    }

    private static ClickHouseConnection CreateConnection(HttpClient http)
        => new("Host=localhost;Port=8123;Database=analytics;Username=default;Compression=false", http);

    private static string[] Values(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Where(part => part.StartsWith("param_p", StringComparison.Ordinal))
        .Select(part => WebUtility.UrlDecode(part.Split('=', 2)[1])).ToArray();

    private sealed class FakeServer : HttpMessageHandler
    {
        public int FailAt { get; init; }
        public Func<CancellationToken, Task>? BeforeResponse { get; init; }
        public List<(string Sql, Uri Uri)> Commands { get; } = [];
        public List<ClickHouseConnectionExtensionsTests.TrackingStream> Streams { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.That(Streams.All(stream => stream.Disposed), Is.True, "Dispose each response before the next insert.");
            Commands.Add((await request.Content!.ReadAsStringAsync(cancellationToken), request.RequestUri!));
            if (Commands.Count == FailAt)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("Code: 60. DB::Exception: Unknown table analytics.orders. (UNKNOWN_TABLE)")
                };
            if (BeforeResponse is not null) await BeforeResponse(cancellationToken);
            var stream = new ClickHouseConnectionExtensionsTests.TrackingStream([]);
            Streams.Add(stream);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }
    }

    [Table("analytics", "regions")]
    public class Region { [Key, Column("id")] public int Id { get; set; } }

    [Table("analytics", "customers")]
    public class Customer
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("region_id"), ForeignKey(typeof(Region), nameof(Region.Id))] public int? RegionId { get; set; }
        [Reference(nameof(RegionId))] public Region? Region { get; set; }
    }

    [Table("analytics", "orders")]
    public class Order
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("buyer_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))] public int BuyerId { get; set; }
        [Column("recipient_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))] public int RecipientId { get; set; }
        [Reference(nameof(BuyerId))] public Customer? Buyer { get; set; }
        [Reference(nameof(RecipientId))] public Customer? Recipient { get; set; }
    }

    [Table("reporting", "case`rows")]
    public class CaseRow
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("ID"), ForeignKey(typeof(Region), nameof(Region.Id))] public int RegionId { get; set; }
        [Reference(nameof(RegionId))] public Region? Region { get; set; }
    }

    [Table("analytics", "recursive")]
    public class RecursiveRow
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("next_id"), ForeignKey(typeof(RecursiveRow), nameof(Id))] public int NextId { get; set; }
        [Reference(nameof(NextId))] public RecursiveRow? Next { get; set; }
    }

    [Table("analytics", "invalid")]
    public class MissingForeignKey
    {
        [Column("id")] public int Id { get; set; }
        [Reference(nameof(Id))] public Region? Region { get; set; }
    }

    [Table("analytics", "invalid")]
    public class MissingKey
    {
        [Column("id"), ForeignKey(typeof(MissingForeignKey), nameof(MissingForeignKey.Id))] public int Id { get; set; }
        [Reference(nameof(Id))] public MissingForeignKey? Child { get; set; }
    }

    [Table("analytics", "invalid")]
    public class WrongKeyType
    {
        [Column("id"), ForeignKey(typeof(Region), nameof(Region.Id))] public string? Id { get; set; }
        [Reference(nameof(Id))] public Region? Region { get; set; }
    }

    [Table("analytics", "invalid")]
    public class DuplicateReference
    {
        [Column("id"), ForeignKey(typeof(Region), nameof(Region.Id))] public int Id { get; set; }
        [Reference(nameof(Id))] public Region? First { get; set; }
        [Reference(nameof(Id))] public Region? Second { get; set; }
    }

    [Table("analytics", "invalid")]
    public class InvalidValue
    {
        [Column("region_id"), ForeignKey(typeof(Region), nameof(Region.Id))] public int RegionId { get; set; }
        [Reference(nameof(RegionId))] public Region? Region { get; set; }
        [Column("unsupported")] public TimeSpan Unsupported { get; set; } = TimeSpan.FromSeconds(1);
    }

    [Table("analytics", "nullable_keys")]
    public class NullableKey { [Key, Column("id")] public int? Id { get; set; } }

    [Table("analytics", "nullable_references")]
    public class NullableReference
    {
        [Column("id"), ForeignKey(typeof(NullableKey), nameof(NullableKey.Id))] public int? Id { get; set; }
        [Reference(nameof(Id))] public NullableKey? Child { get; set; }
    }
}
