using System.Data;
using System.Linq.Expressions;
using System.Reflection;
using Kusto.Data.Common;
using NUnit.Framework;

namespace Akeldov.Data.Kusto.Tests;

[TestFixture]
public class KustoQueryProviderExtensionsTests
{
    [Test]
    public void Select_ReadsByColumnNameDisposesReaderAndLeavesProviderOwnedByCaller()
    {
        using var table = CreateTable();
        var provider = CreateProvider(table, out var fake);
        var rows = provider.Select<User>(row => row.Id >= 1);

        Assert.Multiple(() =>
        {
            Assert.That(fake.Database, Is.EqualTo("analytics"));
            Assert.That(fake.Query, Does.Contain("| where (['user_id'] >= akeldov_p0) | project"));
            Assert.That(fake.Properties!.Parameters["akeldov_p0"], Is.EqualTo("1"));
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0].Id, Is.EqualTo(1));
            Assert.That(rows[0].Name, Is.EqualTo("Alice"));
            Assert.That(rows[1].Score, Is.Null);
            Assert.That(rows[0].Unmapped, Is.EqualTo("default"));
            Assert.That(fake.Reader.IsClosed, Is.True);
            Assert.That(fake.Disposed, Is.False);
        });
    }

    [Test]
    public async Task SelectAsync_PassesCancellationTokenAndMapsRows()
    {
        using var table = CreateTable();
        var provider = CreateProvider(table, out var fake);
        using var cancellation = new CancellationTokenSource();
        var rows = await provider.SelectAsync<User>(row => row.Id > 0, cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(fake.AsyncCalls, Is.EqualTo(1));
            Assert.That(fake.CancellationToken, Is.EqualTo(cancellation.Token));
            Assert.That(rows.Select(row => row.Id), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(fake.Reader.IsClosed, Is.True);
            Assert.That(fake.Disposed, Is.False);
        });
    }

    [Test]
    public async Task SelectAsync_WithoutPredicate_ProjectsMappedColumns()
    {
        using var table = CreateTable();
        var provider = CreateProvider(table, out var fake);
        await provider.SelectAsync<User>();
        Assert.That(fake.Query, Is.EqualTo("['users'] | project ['user_id'], ['name'], ['score']"));
    }

    [Test]
    public void Select_WithoutPredicate_UsesUnfilteredQuery()
    {
        using var table = CreateTable();
        var provider = CreateProvider(table, out var fake);
        provider.Select<User>();
        Assert.That(fake.Query, Is.EqualTo("['users'] | project ['user_id'], ['name'], ['score']"));
    }

    [Test]
    public void Select_EmptyResult_ReturnsEmptyList()
    {
        using var table = CreateTable();
        table.Rows.Clear();
        var provider = CreateProvider(table, out _);
        Assert.That(provider.Select<User>(), Is.Empty);
    }

    [Test]
    public void Select_InvalidMappingOrPredicate_ThrowsBeforeSendingQuery()
    {
        using var table = CreateTable();
        var provider = CreateProvider(table, out var fake);
        Assert.Multiple(() =>
        {
            Assert.That(() => provider.Select<User>(row => row.Id.ToString() == "1"), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => provider.Select<Unmapped>(), Throws.InvalidOperationException);
            Assert.That(() => provider.Select<User>((Expression<Func<User, bool>>)null!), Throws.ArgumentNullException);
            Assert.That(fake.Calls, Is.Zero);
        });
        fake.Reader.Dispose();
    }

    [Test]
    public void SelectAsync_UnsupportedPredicate_ThrowsBeforeSendingQuery()
    {
        using var table = CreateTable();
        var provider = CreateProvider(table, out var fake);
        Assert.That(async () => await provider.SelectAsync<User>(row => row.Id.ToString() == "1"),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(fake.Calls, Is.Zero);
        fake.Reader.Dispose();
    }

    [Test]
    public void SelectAsync_CanceledToken_DoesNotSendQuery()
    {
        using var table = CreateTable();
        var provider = CreateProvider(table, out var fake);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await provider.SelectAsync<User>(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.Calls, Is.Zero);
        fake.Reader.Dispose();
    }

    [Test]
    public void Select_MappingFailure_DisposesReader()
    {
        using var table = CreateTable();
        table.Rows[0]["user_id"] = DBNull.Value;
        var provider = CreateProvider(table, out var fake);
        Assert.That(() => provider.Select<User>(), Throws.InvalidOperationException.With.Message.Contains("not nullable"));
        Assert.That(fake.Reader.IsClosed, Is.True);
        Assert.That(fake.Disposed, Is.False);
    }

    [Test]
    public void Select_SdkStyleIDataReader_MapsWithoutRequiringDbDataReader()
    {
        using var table = CreateTable();
        var provider = CreateProvider(table, out var fake);
        var reader = DispatchProxy.Create<IDataReader, FakeReader>();
        ((FakeReader)reader).Inner = fake.Reader;
        fake.Reader = reader;

        Assert.That(provider.Select<User>().Select(row => row.Id), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(fake.Reader.IsClosed, Is.True);
    }

    [Test]
    public void Select_KustoDatetime_PopulatesDateTimeOffsetInUtc()
    {
        using var table = new DataTable();
        table.Columns.Add("created", typeof(DateTime));
        table.Rows.Add(new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Unspecified));
        var provider = CreateProvider(table, out _);

        var row = provider.Select<DateRow>().Single();
        Assert.That(row.Created, Is.EqualTo(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero)));
        Assert.That(row.Created.Offset, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void SelectAsync_CancellationDuringReading_DisposesReader()
    {
        using var table = CreateTable();
        var provider = CreateProvider(table, out var fake);
        using var cancellation = new CancellationTokenSource();
        var reader = DispatchProxy.Create<IDataReader, FakeReader>();
        ((FakeReader)reader).Inner = fake.Reader;
        ((FakeReader)reader).OnRead = cancellation.Cancel;
        fake.Reader = reader;

        Assert.That(async () => await provider.SelectAsync<User>(cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(reader.IsClosed, Is.True);
        Assert.That(fake.Disposed, Is.False);
    }

    [Test]
    public void NullProvider_Throws()
    {
        ICslQueryProvider provider = null!;
        Assert.Multiple(() =>
        {
            Assert.That(() => provider.Select<User>(), Throws.ArgumentNullException);
            Assert.That(() => provider.Select<User>(row => true), Throws.ArgumentNullException);
            Assert.That(async () => await provider.SelectAsync<User>(), Throws.ArgumentNullException);
        });
    }

    private static DataTable CreateTable()
    {
        var table = new DataTable();
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("score", typeof(int));
        table.Columns.Add("user_id", typeof(int));
        table.Rows.Add("Alice", 10, 1);
        table.Rows.Add("", DBNull.Value, 2);
        return table;
    }

    private static ICslQueryProvider CreateProvider(DataTable table, out FakeProvider fake)
    {
        var provider = DispatchProxy.Create<ICslQueryProvider, FakeProvider>();
        fake = (FakeProvider)provider;
        fake.Reader = table.CreateDataReader();
        return provider;
    }

    public class FakeProvider : DispatchProxy
    {
        public IDataReader Reader { get; set; } = null!;
        public string? Database { get; private set; }
        public string? Query { get; private set; }
        public ClientRequestProperties? Properties { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public int Calls { get; private set; }
        public int AsyncCalls { get; private set; }
        public bool Disposed { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "Dispose")
            {
                Disposed = true;
                return null;
            }

            Calls++;
            Database = (string)args![0]!;
            Query = (string)args[1]!;
            Properties = (ClientRequestProperties)args[2]!;
            if (targetMethod.Name == "ExecuteQueryAsync")
            {
                AsyncCalls++;
                CancellationToken = (CancellationToken)args[3]!;
                return Task.FromResult<IDataReader>(Reader);
            }

            return Reader;
        }
    }

    public class FakeReader : DispatchProxy
    {
        public IDataReader Inner { get; set; } = null!;
        public Action? OnRead { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "Read") OnRead?.Invoke();
            return targetMethod.Invoke(Inner, args);
        }
    }

    [Table("analytics", "dates")]
    public class DateRow
    {
        [Column("created")] public DateTimeOffset Created { get; set; }
    }

    [Table("analytics", "users")]
    public class User
    {
        [Column("user_id")] public int Id { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        public string Unmapped { get; set; } = "default";
    }

    public class Unmapped { }
}
