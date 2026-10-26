using System.Data;
using System.Reflection;
using System.Text.Json;
using Kusto.Data.Common;
using NUnit.Framework;

namespace Akeldov.Data.Kusto.Tests;

[TestFixture]
public class CreateGraphTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_InsertsNestedAndSharedReferencesBeforeOwner(bool asynchronous)
    {
        var provider = CreateProvider(out var fake);
        var customer = new Customer { Id = 12, Region = new Region { Id = 7 } };
        var order = new Order { Id = 34, Buyer = customer, Recipient = customer };
        await Create(provider, order, asynchronous);

        Assert.Multiple(() =>
        {
            Assert.That(fake.Commands.Select(command => command.Database), Is.All.EqualTo("analytics"));
            Assert.That(fake.Commands.Select(command => command.Command.Split(" with ")[0]), Is.EqualTo(new[]
            {
                ".ingest inline into table ['regions']",
                ".ingest inline into table ['customers']",
                ".ingest inline into table ['orders']"
            }));
            Assert.That(Values(fake.Commands[0].Command), Is.EqualTo(new int?[] { 7 }));
            Assert.That(Values(fake.Commands[1].Command), Is.EqualTo(new int?[] { 12, 7 }));
            Assert.That(Values(fake.Commands[2].Command), Is.EqualTo(new int?[] { 34, 12, 12 }));
            Assert.That(customer.RegionId, Is.EqualTo(7));
            Assert.That(order.BuyerId, Is.EqualTo(12));
            Assert.That(order.RecipientId, Is.EqualTo(12));
            Assert.That(fake.Readers.All(reader => reader.IsClosed), Is.True);
            Assert.That(fake.Methods, Is.All.EqualTo(asynchronous ? "ExecuteControlCommandAsync" : "ExecuteControlCommand"));
            Assert.That(fake.Disposed, Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_NullReference_PreservesExistingForeignKey(bool asynchronous)
    {
        var provider = CreateProvider(out var fake);
        await Create(provider, new Customer { Id = 12, RegionId = 7 }, asynchronous);
        Assert.That(fake.Commands, Has.Count.EqualTo(1));
        Assert.That(Values(fake.Commands[0].Command), Is.EqualTo(new int?[] { 12, 7 }));
    }

    [TestCase(false, 1)]
    [TestCase(false, 3)]
    [TestCase(true, 2)]
    [TestCase(true, 3)]
    public void Create_InsertFailure_StopsAndRestoresForeignKeys(bool asynchronous, int failAt)
    {
        var provider = CreateProvider(out var fake);
        fake.FailAt = failAt;
        var customer = new Customer { Id = 12, RegionId = 90, Region = new Region { Id = 7 } };
        var order = new Order { BuyerId = 91, RecipientId = 92, Buyer = customer, Recipient = customer };
        Assert.That(async () => await Create(provider, order, asynchronous),
            Throws.InvalidOperationException.With.Message.EqualTo("ingestion failed"));
        Assert.Multiple(() =>
        {
            Assert.That(fake.Commands, Has.Count.EqualTo(failAt));
            Assert.That(fake.Readers, Has.Count.EqualTo(failAt - 1));
            Assert.That(fake.Readers.All(reader => reader.IsClosed), Is.True);
            Assert.That(customer.RegionId, Is.EqualTo(90));
            Assert.That(order.BuyerId, Is.EqualTo(91));
            Assert.That(order.RecipientId, Is.EqualTo(92));
            Assert.That(fake.Disposed, Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_Cycle_ThrowsBeforeSending(bool asynchronous)
    {
        var provider = CreateProvider(out var fake);
        var first = new RecursiveRow { Id = 1 };
        first.Next = new RecursiveRow { Id = 2, Next = first };
        Assert.That(async () => await Create(provider, first, asynchronous),
            Throws.InvalidOperationException.With.Message.Contains("Cyclic Reference"));
        Assert.That(fake.Commands, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_InvalidReferenceMetadata_ThrowsBeforeSending(bool asynchronous)
    {
        var provider = CreateProvider(out var fake);
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Create(provider, new MissingForeignKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column and ForeignKey"));
            Assert.That(async () => await Create(provider, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Key and Column"));
            Assert.That(async () => await Create(provider, new WrongKeyType(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("incompatible foreign key and key types"));
            Assert.That(async () => await Create(provider, new UnreadableReference(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("public getter"));
            Assert.That(async () => await Create(provider, new DuplicateReference(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("already assigned"));
            Assert.That(fake.Commands, Is.Empty);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_LateMappingFailure_RestoresKeysBeforeSending(bool asynchronous)
    {
        var provider = CreateProvider(out var fake);
        var row = new InvalidValue { RegionId = 99, Region = new Region { Id = 7 } };
        Assert.That(async () => await Create(provider, row, asynchronous), Throws.TypeOf<NotSupportedException>());
        Assert.That(row.RegionId, Is.EqualTo(99));
        Assert.That(fake.Commands, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_DistinctInstancesWithSameKey_AreNotMerged(bool asynchronous)
    {
        var provider = CreateProvider(out var fake);
        await Create(provider, new Order
        {
            Buyer = new Customer { Id = 12 }, Recipient = new Customer { Id = 12 }
        }, asynchronous);
        Assert.That(fake.Commands, Has.Count.EqualTo(3));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_NullReferencedKey_ThrowsBeforeSending(bool asynchronous)
    {
        var provider = CreateProvider(out var fake);
        Assert.That(async () => await Create(provider, new NullableReference { Child = new NullableKey() }, asynchronous),
            Throws.InvalidOperationException.With.Message.Contains("null referenced key"));
        Assert.That(fake.Commands, Is.Empty);
    }

    [Test]
    public void CreateAsync_CancellationBetweenCommands_StopsAndRestoresKeys()
    {
        var provider = CreateProvider(out var fake);
        using var cancellation = new CancellationTokenSource();
        fake.AfterCommand = cancellation.Cancel;
        var row = new Customer { Id = 12, RegionId = 99, Region = new Region { Id = 7 } };
        Assert.That(async () => await provider.CreateAsync(row, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.Commands, Has.Count.EqualTo(1));
        Assert.That(fake.Readers.Single().IsClosed, Is.True);
        Assert.That(row.RegionId, Is.EqualTo(99));
    }

    [Test]
    public void CreateAsync_PreCanceled_DoesNotPrepareOrSendGraph()
    {
        var provider = CreateProvider(out var fake);
        var row = new Customer { RegionId = 99, Region = new Region { Id = 7 } };
        Assert.That(async () => await provider.CreateAsync(row, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(row.RegionId, Is.EqualTo(99));
        Assert.That(fake.Commands, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_UsesEachDatabaseAndPreservesCaseDistinctColumns(bool asynchronous)
    {
        var provider = CreateProvider(out var fake);
        await Create(provider, new CaseRow { Id = 10, Region = new Region { Id = 7 } }, asynchronous);
        Assert.That(fake.Commands.Select(command => command.Database), Is.EqualTo(new[] { "analytics", "reporting" }));
        Assert.That(fake.Commands[1].Command, Does.StartWith(".ingest inline into table ['case\\'rows'] with "));
        Assert.That(fake.Commands[1].Command, Does.Contain("\"column\":\"id\""));
        Assert.That(fake.Commands[1].Command, Does.Contain("\"column\":\"ID\""));
        Assert.That(Values(fake.Commands[1].Command), Is.EqualTo(new int?[] { 10, 7 }));
    }

    [Test]
    public async Task CreateAsync_AwaitsDependencyBeforeSendingOwner()
    {
        var provider = CreateProvider(out var fake);
        var pending = new TaskCompletionSource<IDataReader>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.CompleteAsync = reader => fake.Commands.Count == 1 ? pending.Task : Task.FromResult(reader);
        var creation = provider.CreateAsync(new Customer { Id = 12, Region = new Region { Id = 7 } });
        Assert.That(fake.Commands, Has.Count.EqualTo(1));
        Assert.That(creation.IsCompleted, Is.False);
        pending.SetResult(fake.Readers[0]);
        await creation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fake.Commands, Has.Count.EqualTo(2));
        Assert.That(fake.Readers.All(reader => reader.IsClosed), Is.True);
    }

    [Table("reporting", "case'rows")]
    public class CaseRow
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("ID"), ForeignKey(typeof(Region), nameof(Region.Id))] public int RegionId { get; set; }
        [Reference(nameof(RegionId))] public Region? Region { get; set; }
    }

    private static Task Create<T>(ICslAdminProvider provider, T entity, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return provider.CreateAsync(entity);
        provider.Create(entity);
        return Task.CompletedTask;
    }

    private static ICslAdminProvider CreateProvider(out FakeAdmin fake)
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        fake = (FakeAdmin)provider;
        return provider;
    }

    private static int?[] Values(string command)
    {
        using var data = JsonDocument.Parse(command.Split("<|\n")[1]);
        return data.RootElement.EnumerateObject().Select(property =>
            property.Value.ValueKind == JsonValueKind.Null ? (int?)null : property.Value.GetInt32()).ToArray();
    }

    public class FakeAdmin : DispatchProxy
    {
        public List<(string Database, string Command)> Commands { get; } = [];
        public List<IDataReader> Readers { get; } = [];
        public List<string> Methods { get; } = [];
        public int FailAt { get; set; }
        public Action? AfterCommand { get; set; }
        public Func<IDataReader, Task<IDataReader>>? CompleteAsync { get; set; }
        public bool Disposed { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "Dispose") { Disposed = true; return null; }
            Assert.That(targetMethod.Name, Is.AnyOf("ExecuteControlCommand", "ExecuteControlCommandAsync"));
            Assert.That(Readers.All(reader => reader.IsClosed), Is.True, "Dispose each result before the next ingestion.");
            Commands.Add(((string)args![0]!, (string)args[1]!));
            Methods.Add(targetMethod.Name);
            if (Commands.Count == FailAt)
            {
                var error = new InvalidOperationException("ingestion failed");
                if (targetMethod.Name == "ExecuteControlCommandAsync") return Task.FromException<IDataReader>(error);
                throw error;
            }
            IDataReader reader = new DataTable().CreateDataReader();
            Readers.Add(reader);
            AfterCommand?.Invoke();
            return targetMethod.Name == "ExecuteControlCommandAsync"
                ? CompleteAsync?.Invoke(reader) ?? Task.FromResult(reader) : reader;
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
    public class UnreadableReference
    {
        [Column("id"), ForeignKey(typeof(Region), nameof(CreateGraphTests.Region.Id))] public int Id { get; set; }
        [Reference(nameof(Id))] public Region Region { set { } }
    }

    [Table("analytics", "invalid")]
    public class InvalidValue
    {
        [Column("region_id"), ForeignKey(typeof(Region), nameof(Region.Id))] public int RegionId { get; set; }
        [Reference(nameof(RegionId))] public Region? Region { get; set; }
        [Column("unsupported")] public object Unsupported { get; set; } = new();
    }

    [Table("analytics", "invalid")]
    public class DuplicateReference
    {
        [Column("id"), ForeignKey(typeof(Region), nameof(Region.Id))] public int Id { get; set; }
        [Reference(nameof(Id))] public Region? First { get; set; }
        [Reference(nameof(Id))] public Region? Second { get; set; }
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
