using System.Data;
using System.Reflection;
using System.Text.Json;
using Kusto.Data.Common;
using NUnit.Framework;

namespace Akeldov.Data.Kusto.Tests;

[TestFixture]
public class KustoAdminProviderExtensionsTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_MapsColumnsByNameAndDisposesResult(bool asynchronous)
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        var fake = (FakeAdmin)provider;
        var entity = new Row { Id = 5, Name = "quote'\"\n.ingest inline into table other <| 1", Created = DateTime.UnixEpoch };
        if (asynchronous) await provider.CreateAsync(entity);
        else provider.Create(entity);
        Assert.Multiple(() =>
        {
            Assert.That(fake.Database, Is.EqualTo("analytics"));
            Assert.That(fake.Command, Does.StartWith(".ingest inline into table ['odd\\'table'] with (format='json', ingestionMapping="));
            Assert.That(fake.Command, Does.Contain("$.p0"));
            Assert.That(fake.Command, Does.Not.Contain("Unmapped"));
            Assert.That(fake.Reader.IsClosed, Is.True);
            Assert.That(fake.Disposed, Is.False);
        });
        using var data = JsonDocument.Parse(fake.Command!.Split("<|\n")[1]);
        Assert.That(data.RootElement.GetProperty("p0").GetInt32(), Is.EqualTo(5));
        Assert.That(data.RootElement.GetProperty("p1").GetString(), Is.EqualTo(entity.Name));
        Assert.That(data.RootElement.GetProperty("p2").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(data.RootElement.GetProperty("p3").GetDateTime(), Is.EqualTo(DateTime.UnixEpoch));
    }

    [Test]
    public void Create_RejectsInvalidValuesAndCancellationBeforeSending()
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        var fake = (FakeAdmin)provider;
        Assert.That(() => provider.Create<Row>(null!), Throws.ArgumentNullException);
        Assert.That(() => provider.Create(new Unsupported()), Throws.TypeOf<NotSupportedException>());
        Assert.That(async () => await provider.CreateAsync(new Row(), new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.Command, Is.Null);
        fake.Reader.Dispose();
    }

    public class FakeAdmin : DispatchProxy
    {
        public IDataReader Reader { get; } = new DataTable().CreateDataReader();
        public string? Database { get; private set; }
        public string? Command { get; private set; }
        public bool Disposed { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "Dispose") { Disposed = true; return null; }
            Database = (string)args![0]!;
            Command = (string)args[1]!;
            return targetMethod.Name == "ExecuteControlCommandAsync" ? Task.FromResult(Reader) : Reader;
        }
    }

    [Table("analytics", "odd'table")]
    public class Row
    {
        [Column("id")] public int Id { get; set; }
        [Column("odd'name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("created")] public DateTime Created { get; set; }
        public string Unmapped { get; set; } = "ignored";
    }
    [Table("analytics", "rows")]
    public class Unsupported { [Column("amount")] public decimal Amount { get; set; } }
}
