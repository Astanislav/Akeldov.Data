using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using NUnit.Framework;

namespace Akeldov.Data.MongoDb.Tests;

[TestFixture]
public class CreateGraphTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_InsertsNestedAndSharedReferencesBeforeOwner(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        var region = new Region { Id = ObjectId.GenerateNewId() };
        var customer = new Customer { Id = Guid.NewGuid(), Region = region };
        var order = new Order { Id = Guid.NewGuid(), Buyer = customer, Recipient = customer };
        await Create(client, order, asynchronous);

        Assert.Multiple(() =>
        {
            Assert.That(fake.Inserts.Select(insert => insert.Database), Is.All.EqualTo("analytics"));
            Assert.That(fake.Inserts.Select(insert => insert.Collection), Is.EqualTo(new[] { "regions", "customers", "orders" }));
            Assert.That(fake.Inserts[0].Document, Is.EqualTo(new BsonDocument("_id", region.Id)));
            Assert.That(fake.Inserts[1].Document, Is.EqualTo(new BsonDocument
            {
                { "_id", Uuid(customer.Id) }, { "region_id", region.Id }
            }));
            Assert.That(fake.Inserts[2].Document, Is.EqualTo(new BsonDocument
            {
                { "_id", Uuid(order.Id) }, { "buyer_id", Uuid(customer.Id) }, { "recipient_id", Uuid(customer.Id) }
            }));
            Assert.That(customer.RegionId, Is.EqualTo(region.Id));
            Assert.That(order.BuyerId, Is.EqualTo(customer.Id));
            Assert.That(order.RecipientId, Is.EqualTo(customer.Id));
            Assert.That(fake.Methods, Is.All.EqualTo(asynchronous ? "InsertOneAsync" : "InsertOne"));
            Assert.That(fake.Disposed, Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_NullReference_PreservesExistingForeignKey(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        var regionId = ObjectId.GenerateNewId();
        var customer = new Customer { Id = Guid.NewGuid(), RegionId = regionId };
        await Create(client, customer, asynchronous);
        Assert.That(fake.Inserts, Has.Count.EqualTo(1));
        Assert.That(fake.Inserts[0].Document["region_id"].AsObjectId, Is.EqualTo(regionId));
        Assert.That(customer.RegionId, Is.EqualTo(regionId));
    }

    [Test]
    public void Create_InsertFailure_StopsAndRestoresForeignKeys(
        [Values] bool asynchronous, [Values(1, 2, 3)] int failAt)
    {
        var client = CreateClient(out var fake);
        fake.FailAt = failAt;
        var originalId = Guid.NewGuid();
        var customer = new Customer { Id = Guid.NewGuid(), Region = new Region { Id = ObjectId.GenerateNewId() } };
        var order = new Order { BuyerId = originalId, RecipientId = originalId, Buyer = customer, Recipient = customer };
        Assert.That(async () => await Create(client, order, asynchronous),
            Throws.InvalidOperationException.With.Message.EqualTo("insert failed"));
        Assert.Multiple(() =>
        {
            Assert.That(fake.Inserts, Has.Count.EqualTo(failAt));
            Assert.That(customer.RegionId, Is.Null);
            Assert.That(order.BuyerId, Is.EqualTo(originalId));
            Assert.That(order.RecipientId, Is.EqualTo(originalId));
            Assert.That(fake.Disposed, Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_InvalidGraph_DoesNotAccessDatabase(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        var first = new RecursiveRow { Id = 1 };
        first.Next = new RecursiveRow { Id = 2, Next = first };
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Create(client, first, asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Cyclic Reference"));
            Assert.That(async () => await Create(client, new MissingForeignKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column and ForeignKey"));
            Assert.That(async () => await Create(client, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Key and Column"));
            Assert.That(async () => await Create(client, new WrongKeyType(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("incompatible foreign key and key types"));
            Assert.That(async () => await Create(client, new DuplicateReference(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("already assigned"));
            Assert.That(async () => await Create(client, new UnreadableReference(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("public getter"));
            Assert.That(async () => await Create(client, new StringReference { Child = new StringKey() }, asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("null referenced key"));
            Assert.That(fake.DatabaseCalls, Is.Zero);
            Assert.That(fake.Inserts, Is.Empty);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_SerializationFailure_RestoresKeysBeforeAccessingDatabase(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        var originalId = ObjectId.GenerateNewId();
        var row = new InvalidValue { RegionId = originalId, Region = new Region { Id = ObjectId.GenerateNewId() } };
        Assert.That(async () => await Create(client, row, asynchronous),
            Throws.TypeOf<BsonSerializationException>().With.Message.EqualTo("invalid payload"));
        Assert.That(row.RegionId, Is.EqualTo(originalId));
        Assert.That(fake.DatabaseCalls, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_InvalidChildField_DoesNotAccessDatabase(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        Assert.That(async () => await Create(client, new InvalidFieldOwner { Child = new InvalidField() }, asynchronous),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("top-level field"));
        Assert.That(fake.DatabaseCalls, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_DistinctInstancesWithSameKey_AreNotMerged(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        var id = Guid.NewGuid();
        await Create(client, new Order { Buyer = new Customer { Id = id }, Recipient = new Customer { Id = id } }, asynchronous);
        Assert.That(fake.Inserts, Has.Count.EqualTo(3));
        Assert.That(fake.Inserts[0].Document, Is.EqualTo(fake.Inserts[1].Document));
        Assert.That(fake.Inserts[0].Document["region_id"], Is.EqualTo(BsonNull.Value));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_UsesEachDatabaseAndPreservesCaseDistinctFields(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        var row = new CaseRow { Id = 10, Region = new Region { Id = ObjectId.GenerateNewId() } };
        await Create(client, row, asynchronous);
        Assert.That(fake.Inserts.Select(insert => insert.Database), Is.EqualTo(new[] { "analytics", "reporting" }));
        Assert.That(fake.Inserts[1].Collection, Is.EqualTo("case_rows"));
        Assert.That(fake.Inserts[1].Document, Is.EqualTo(new BsonDocument { { "id", 10 }, { "ID", row.Region.Id } }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_StringKey_IsCopiedAndStoredAsData(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        var row = new StringReference { Child = new StringKey { Id = "customer.$'\"\n42" } };
        await Create(client, row, asynchronous);
        Assert.That(row.Id, Is.EqualTo(row.Child.Id));
        Assert.That(fake.Inserts[0].Document["_id"].AsString, Is.EqualTo(row.Id));
        Assert.That(fake.Inserts[1].Document["child_id"].AsString, Is.EqualTo(row.Id));
        Assert.That(fake.Inserts[1].Document.Contains("Child"), Is.False);
    }

    [Test]
    public async Task CreateAsync_AwaitsDependencyAndPassesCancellationToEveryInsert()
    {
        var client = CreateClient(out var fake);
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.InsertAsync = _ => fake.Inserts.Count == 1 ? pending.Task : Task.CompletedTask;
        var creation = client.CreateAsync(new Customer { Region = new Region { Id = ObjectId.GenerateNewId() } }, cancellation.Token);
        try
        {
            Assert.That(fake.Inserts, Has.Count.EqualTo(1));
            Assert.That(creation.IsCompleted, Is.False);
        }
        finally { pending.SetResult(); }
        await creation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fake.Inserts, Has.Count.EqualTo(2));
        Assert.That(fake.Tokens, Is.All.EqualTo(cancellation.Token));
    }

    [Test]
    public void CreateAsync_CancellationBetweenInserts_StopsAndRestoresKeys()
    {
        var client = CreateClient(out var fake);
        using var cancellation = new CancellationTokenSource();
        fake.AfterInsert = cancellation.Cancel;
        var row = new Customer { Region = new Region { Id = ObjectId.GenerateNewId() } };
        Assert.That(async () => await client.CreateAsync(row, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.Inserts, Has.Count.EqualTo(1));
        Assert.That(fake.DatabaseCalls, Is.EqualTo(1));
        Assert.That(row.RegionId, Is.Null);
        Assert.That(fake.Disposed, Is.False);
    }

    [Test]
    public void CreateAsync_CancellationDuringInsert_RestoresKeys()
    {
        var client = CreateClient(out var fake);
        using var cancellation = new CancellationTokenSource();
        fake.InsertAsync = token =>
        {
            cancellation.Cancel();
            return Task.Delay(Timeout.Infinite, token);
        };
        var row = new Customer { Region = new Region { Id = ObjectId.GenerateNewId() } };
        Assert.That(async () => await client.CreateAsync(row, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.Inserts, Has.Count.EqualTo(1));
        Assert.That(row.RegionId, Is.Null);
    }

    [Test]
    public void CreateAsync_PreCanceled_DoesNotPrepareOrAccessDatabase()
    {
        var client = CreateClient(out var fake);
        var row = new Customer { Region = new Region { Id = ObjectId.GenerateNewId() } };
        Assert.That(async () => await client.CreateAsync(row, new CancellationToken(true)), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.DatabaseCalls, Is.Zero);
        Assert.That(row.RegionId, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Create_NullArguments_Throw(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        Assert.That(async () => await Create<Customer>(client, null!, asynchronous), Throws.ArgumentNullException);
        Assert.That(async () => await Create(null!, new Customer(), asynchronous), Throws.ArgumentNullException);
        Assert.That(fake.DatabaseCalls, Is.Zero);
    }

    private static BsonBinaryData Uuid(Guid id) => new(id, GuidRepresentation.Standard);

    private static Task Create<T>(IMongoClient client, T entity, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return client.CreateAsync(entity);
        client.Create(entity);
        return Task.CompletedTask;
    }

    private static IMongoClient CreateClient(out FakeMongo fake)
    {
        var client = DispatchProxy.Create<IMongoClient, FakeMongo>();
        fake = (FakeMongo)client;
        return client;
    }

    public class FakeMongo : DispatchProxy
    {
        private FakeMongo? root;
        private string database = "";
        private string collection = "";
        public List<(string Database, string Collection, BsonDocument Document)> Inserts { get; } = [];
        public List<string> Methods { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public int DatabaseCalls { get; private set; }
        public int FailAt { get; set; }
        public Action? AfterInsert { get; set; }
        public Func<CancellationToken, Task>? InsertAsync { get; set; }
        public bool Disposed { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var state = root ?? this;
            switch (targetMethod!.Name)
            {
                case "GetDatabase":
                    state.DatabaseCalls++;
                    var db = DispatchProxy.Create<IMongoDatabase, FakeMongo>();
                    ((FakeMongo)db).root = state;
                    ((FakeMongo)db).database = (string)args![0]!;
                    return db;
                case "GetCollection":
                    var coll = DispatchProxy.Create<IMongoCollection<BsonDocument>, FakeMongo>();
                    ((FakeMongo)coll).root = state;
                    ((FakeMongo)coll).database = database;
                    ((FakeMongo)coll).collection = (string)args![0]!;
                    return coll;
                case "InsertOne":
                case "InsertOneAsync":
                    state.Inserts.Add((database, collection, ((BsonDocument)args![0]!).DeepClone().AsBsonDocument));
                    state.Methods.Add(targetMethod.Name);
                    var token = (CancellationToken)args[2]!;
                    state.Tokens.Add(token);
                    if (state.Inserts.Count == state.FailAt)
                    {
                        var error = new InvalidOperationException("insert failed");
                        if (targetMethod.Name == "InsertOneAsync") return Task.FromException(error);
                        throw error;
                    }
                    state.AfterInsert?.Invoke();
                    return targetMethod.Name == "InsertOneAsync" ? state.InsertAsync?.Invoke(token) ?? Task.CompletedTask : null;
                case "Dispose":
                    state.Disposed = true;
                    return null;
                default: throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    [Table("analytics", "regions")]
    public class Region { [Key, Column("_id")] public ObjectId Id { get; set; } }

    [Table("analytics", "customers")]
    public class Customer
    {
        [Key, Column("_id")] public Guid Id { get; set; }
        [Column("region_id"), ForeignKey(typeof(Region), nameof(Region.Id))] public ObjectId? RegionId { get; set; }
        [Reference(nameof(RegionId))] public Region? Region { get; set; }
    }

    [Table("analytics", "orders")]
    public class Order
    {
        [Key, Column("_id")] public Guid Id { get; set; }
        [Column("buyer_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))] public Guid BuyerId { get; set; }
        [Column("recipient_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))] public Guid? RecipientId { get; set; }
        [Reference(nameof(BuyerId))] public Customer? Buyer { get; set; }
        [Reference(nameof(RecipientId))] public Customer? Recipient { get; set; }
    }

    [Table("reporting", "case_rows")]
    public class CaseRow
    {
        [Column("id")] public int Id { get; set; }
        [Column("ID"), ForeignKey(typeof(Region), nameof(Region.Id))] public ObjectId RegionId { get; set; }
        [Reference(nameof(RegionId))] public Region? Region { get; set; }
    }

    [Table("analytics", "recursive")]
    public class RecursiveRow
    {
        [Key, Column("_id")] public int Id { get; set; }
        [Column("next_id"), ForeignKey(typeof(RecursiveRow), nameof(Id))] public int NextId { get; set; }
        [Reference(nameof(NextId))] public RecursiveRow? Next { get; set; }
    }

    [Table("analytics", "invalid")]
    public class MissingForeignKey
    {
        [Column("id")] public ObjectId Id { get; set; }
        [Reference(nameof(Id))] public Region? Region { get; set; }
    }

    [Table("analytics", "invalid")]
    public class MissingKey
    {
        [Column("id"), ForeignKey(typeof(MissingForeignKey), nameof(MissingForeignKey.Id))] public ObjectId Id { get; set; }
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
        [Column("id"), ForeignKey(typeof(Region), nameof(Region.Id))] public ObjectId Id { get; set; }
        [Reference(nameof(Id))] public Region? First { get; set; }
        [Reference(nameof(Id))] public Region? Second { get; set; }
    }

    [Table("analytics", "invalid")]
    public class UnreadableReference
    {
        [Column("id"), ForeignKey(typeof(Region), nameof(CreateGraphTests.Region.Id))] public ObjectId Id { get; set; }
        [Reference(nameof(Id))] public Region Region { set { } }
    }

    [Table("analytics", "invalid")]
    public class InvalidValue
    {
        [Column("region_id"), ForeignKey(typeof(Region), nameof(Region.Id))] public ObjectId RegionId { get; set; }
        [Reference(nameof(RegionId))] public Region? Region { get; set; }
        [Column("payload")] public InvalidPayload Payload { get; set; } = new();
    }

    [BsonSerializer(typeof(InvalidPayloadSerializer))]
    public class InvalidPayload { }

    public class InvalidPayloadSerializer : SerializerBase<InvalidPayload>
    {
        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, InvalidPayload value)
            => throw new BsonSerializationException("invalid payload");
    }

    [Table("analytics", "invalid")]
    public class InvalidField { [Key, Column("nested.id")] public int Id { get; set; } }

    [Table("analytics", "invalid")]
    public class InvalidFieldOwner
    {
        [Column("id"), ForeignKey(typeof(InvalidField), nameof(InvalidField.Id))] public int Id { get; set; }
        [Reference(nameof(Id))] public InvalidField? Child { get; set; }
    }

    [Table("analytics", "string_keys")]
    public class StringKey { [Key, Column("_id")] public string? Id { get; set; } }

    [Table("analytics", "string_references")]
    public class StringReference
    {
        [Column("child_id"), ForeignKey(typeof(StringKey), nameof(StringKey.Id))] public string? Id { get; set; }
        [Reference(nameof(Id))] public StringKey? Child { get; set; }
    }
}
