using System.Data;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using ClickHouse.Driver.ADO;
using NUnit.Framework;

namespace Akeldov.Data.ClickHouse.Tests;

[TestFixture]
public class ClickHouseConnectionExtensionsTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Select_ReadsDriverBinaryResponseAndPreservesConnectionState(bool initiallyOpen)
    {
        using var handler = new FakeServer();
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        if (initiallyOpen) connection.Open();
        var rows = connection.Select<User>();
        AssertRows(rows);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        Assert.That(handler.ResponseStream!.Disposed, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SelectAsync_ReadsDriverBinaryResponseAndPreservesConnectionState(bool initiallyOpen)
    {
        using var handler = new FakeServer();
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        if (initiallyOpen) await connection.OpenAsync();
        var rows = await connection.SelectAsync<User>(row => row.Id >= 1);
        AssertRows(rows);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        Assert.That(handler.Sql, Does.Contain("`user_id` >= {p0:Int32}"));
        Assert.That(handler.ResponseStream!.Disposed, Is.True);
    }

    [Test]
    public void Select_UsesNativeParametersWithoutEmbeddingTheirValuesInSql()
    {
        const string value = "Robert'); DROP TABLE analytics.users;-- Ольга";
        using var handler = new FakeServer();
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        connection.Select<User>(row => row.Name == value);
        Assert.Multiple(() =>
        {
            Assert.That(handler.Sql, Does.Contain("`name` = {p0:String}"));
            Assert.That(handler.Sql, Does.Not.Contain(value));
            Assert.That(WebUtility.UrlDecode(handler.Uri!.Query), Does.Contain("param_p0="));
            Assert.That(WebUtility.UrlDecode(handler.Uri.Query), Does.Contain("DROP TABLE"));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SelectAsync_ServerError_PreservesConnectionState(bool initiallyOpen)
    {
        using var handler = new FakeServer { Fail = true };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        if (initiallyOpen) connection.Open();
        Assert.That(async () => await connection.SelectAsync<User>(), Throws.Exception);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    [Test]
    public void Select_TypedParameters_PreserveUuidDecimalAndFractionalSecondsOnTheWire()
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var date = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        var amount = 10.1234m;
        using var handler = new FakeServer
        {
            ResponseData = BinaryResponse(["external_id", "created", "amount"], ["UUID", "DateTime64(7, 'UTC')", "Decimal(38, 4)"])
        };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        Assert.That(connection.Select<TypedRow>(row => row.Id == id && row.Created >= date && row.Amount == amount), Is.Empty);
        var query = WebUtility.UrlDecode(handler.Uri!.Query);
        Assert.Multiple(() =>
        {
            Assert.That(handler.Sql, Does.Contain("{p0:UUID}"));
            Assert.That(handler.Sql, Does.Contain("{p1:DateTime64(7, 'UTC')}"));
            Assert.That(handler.Sql, Does.Contain("{p2:Decimal(38, 4)}"));
            Assert.That(query, Does.Contain(id.ToString()));
            Assert.That(query, Does.Contain(".1234567"));
            Assert.That(query, Does.Contain("10.1234"));
        });
    }

    [Test]
    public void Select_ParameterLikeIdentifier_IsNotRewrittenByDriver()
    {
        using var handler = new FakeServer { ResponseData = BinaryResponse(["@p0"], ["Int32"]) };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        Assert.That(connection.Select<ParameterLikeRow>(row => row.Id == 7), Is.Empty);
        Assert.That(handler.Sql, Does.Contain("SELECT `@p0` FROM `analytics`.`@p0` WHERE (`@p0` = {p0:Int32})"));
    }

    [Test]
    public void Select_ReadsColumnsWhoseNamesDifferOnlyInCase()
    {
        using var handler = new FakeServer
        {
            ResponseData = BinaryResponse(["id", "ID"], ["Int32", "Int32"], writer => { writer.Write(1); writer.Write(2); })
        };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        var row = connection.Select<ClickHouseDialectTests.CaseRow>().Single();
        Assert.That(row.Id, Is.EqualTo(1));
        Assert.That(row.OtherId, Is.EqualTo(2));
    }

    [Test]
    public async Task SelectAsync_ReadsNativeUuidDecimalAndDateTime64()
    {
        var date = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        using var handler = new FakeServer
        {
            ResponseData = BinaryResponse(["external_id", "created", "amount"], ["UUID", "DateTime64(7, 'UTC')", "Decimal(18, 4)"], writer =>
            {
                writer.Write(new byte[16]); // Guid.Empty in the native UUID representation.
                writer.Write(date.Ticks - DateTime.UnixEpoch.Ticks);
                writer.Write(101234L); // 10.1234 with four fractional digits.
            })
        };
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        var row = (await connection.SelectAsync<TypedRow>()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.Id, Is.EqualTo(Guid.Empty));
            Assert.That(row.Created, Is.EqualTo(date));
            Assert.That(row.Created.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(row.Amount, Is.EqualTo(10.1234m));
        });
    }

    [Test]
    public void SelectAsync_CanceledToken_DoesNotSendRequest()
    {
        using var handler = new FakeServer();
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.That(async () => await connection.SelectAsync<User>(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(handler.Requests, Is.Zero);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void SelectAsync_CancellationDuringMapping_DisposesReaderAndClosesConnection()
    {
        using var handler = new FakeServer();
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        using var cancellation = new CancellationTokenSource();
        CancelingUser.OnName = cancellation.Cancel;
        try
        {
            Assert.That(async () => await connection.SelectAsync<CancelingUser>(cancellation.Token),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(handler.ResponseStream!.Disposed, Is.True);
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        }
        finally { CancelingUser.OnName = null; }
    }

    [Test]
    public void Select_MappingError_DisposesResponseAndClosesConnection()
    {
        using var handler = new FakeServer();
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        Assert.That(() => connection.Select<NonNullableScore>(), Throws.InvalidOperationException.With.Message.Contains("not nullable"));
        Assert.That(handler.ResponseStream!.Disposed, Is.True);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void InvalidMappingOrPredicate_DoesNotOpenConnectionOrSendRequest()
    {
        using var handler = new FakeServer();
        using var http = new HttpClient(handler);
        using var connection = CreateConnection(http);
        Assert.Multiple(() =>
        {
            Assert.That(() => connection.Select<User>((Expression<Func<User, bool>>)null!), Throws.ArgumentNullException);
            Assert.That(() => connection.Select<Unmapped>(), Throws.InvalidOperationException);
            Assert.That(() => connection.Select<User>(row => row.Id.ToString() == "1"), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => connection.Select<User>(row => row.Unmapped == "value"), Throws.TypeOf<NotSupportedException>());
            Assert.That(async () => await connection.SelectAsync<User>(row => row.Id.ToString() == "1"), Throws.TypeOf<NotSupportedException>());
            Assert.That(async () => await connection.SelectAsync<User>((Expression<Func<User, bool>>)null!), Throws.ArgumentNullException);
            Assert.That(handler.Requests, Is.Zero);
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void NullConnection_Throws()
    {
        ClickHouseConnection connection = null!;
        Assert.Multiple(() =>
        {
            Assert.That(() => connection.Select<User>(), Throws.ArgumentNullException);
            Assert.That(() => connection.Select<User>(row => true), Throws.ArgumentNullException);
            Assert.That(async () => await connection.SelectAsync<User>(), Throws.ArgumentNullException);
            Assert.That(async () => await connection.SelectAsync<User>(row => true), Throws.ArgumentNullException);
        });
    }

    private static ClickHouseConnection CreateConnection(HttpClient http)
        => new("Host=localhost;Port=8123;Database=analytics;Username=default;Compression=false", http);

    private static byte[] BinaryResponse(string[] names, string[] types, Action<BinaryWriter>? writeRows = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write7BitEncodedInt(names.Length);
        foreach (var name in names) writer.Write(name);
        foreach (var type in types) writer.Write(type);
        writeRows?.Invoke(writer);
        return stream.ToArray();
    }

    private static void AssertRows(List<User> rows)
    {
        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(row => row.Id), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(rows[0].Name, Is.EqualTo("Ольга"));
            Assert.That(rows[0].Score, Is.EqualTo(10));
            Assert.That(rows[0].Active, Is.True);
            Assert.That(rows[1].Name, Is.Null);
            Assert.That(rows[1].Score, Is.Null);
            Assert.That(rows[1].Active, Is.False);
            Assert.That(rows[0].Unmapped, Is.EqualTo("default"));
        });
    }

    public sealed class FakeServer : HttpMessageHandler
    {
        public bool Fail { get; init; }
        public byte[]? ResponseData { get; init; }
        public int Requests { get; private set; }
        public string? Sql { get; private set; }
        public Uri? Uri { get; private set; }
        public TrackingStream? ResponseStream { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Uri = request.RequestUri;
            Sql = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Fail) return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                { Content = new StringContent("Code: 60. DB::Exception: Unknown table analytics.users. (UNKNOWN_TABLE)") };
            ResponseStream = new TrackingStream(ResponseData ?? CreateBinaryResponse());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(ResponseStream) };
        }

        // RowBinaryWithNamesAndTypes fixture exercises the actual driver's result decoder.
        private static byte[] CreateBinaryResponse()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write7BitEncodedInt(4);
            foreach (var name in new[] { "name", "score", "is_active", "user_id" }) writer.Write(name);
            foreach (var type in new[] { "Nullable(String)", "Nullable(Int32)", "Bool", "Int32" }) writer.Write(type);
            writer.Write((byte)0); writer.Write("Ольга");
            writer.Write((byte)0); writer.Write(10);
            writer.Write(true); writer.Write(1);
            writer.Write((byte)1); writer.Write((byte)1);
            writer.Write(false); writer.Write(2);
            return stream.ToArray();
        }
    }

    public sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Table("analytics", "users")]
    public class User
    {
        [Column("user_id")] public int Id { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("is_active")] public bool Active { get; set; }
        public string Unmapped { get; set; } = "default";
    }
    [Table("analytics", "users")]
    public class CancelingUser
    {
        public static Action? OnName { get; set; }
        [Column("name")] public string? Name { get => null; set => OnName?.Invoke(); }
    }
    [Table("analytics", "users")]
    public class NonNullableScore { [Column("score")] public int Score { get; set; } }
    [Table("analytics", "typed_rows")]
    public class TypedRow
    {
        [Column("external_id")] public Guid Id { get; set; }
        [Column("created")] public DateTime Created { get; set; }
        [Column("amount")] public decimal Amount { get; set; }
    }
    [Table("analytics", "@p0")]
    public class ParameterLikeRow { [Column("@p0")] public int Id { get; set; } }
    public class Unmapped { }
}
