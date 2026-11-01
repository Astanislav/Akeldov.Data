using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using NUnit.Framework;

namespace Akeldov.Data.MongoDb.Tests;

[TestFixture]
public class UpdateTests
{
    [Test]
    public void CreateUpdate_UsesCompositeKeysAndMappedBsonValues()
    {
        var tenantId = Guid.NewGuid();
        var created = new DateTime(2026, 11, 1, 12, 34, 56, DateTimeKind.Utc);
        var entity = new Row
        {
            Id = 12, TenantId = tenantId, Name = "O'Brien'); db.rows.drop();--", Score = null,
            State = State.Active, Created = created, Payload = [0, 1, 255]
        };

        var operation = MongoClientExtensions.CreateUpdate(entity);

        Assert.Multiple(() =>
        {
            Assert.That(operation.Database, Is.EqualTo("analytics"));
            Assert.That(operation.Collection, Is.EqualTo("rows"));
            Assert.That(operation.Filter, Is.EqualTo(new BsonDocument
            {
                { "business_id", 12 },
                { "tenant_id", new BsonBinaryData(tenantId, GuidRepresentation.Standard) }
            }));
            Assert.That(operation.Update, Is.EqualTo(new BsonDocument("$set", new BsonDocument
            {
                { "name", entity.Name }, { "score", BsonNull.Value }, { "state", 1 },
                { "created", new BsonDateTime(created) }, { "payload", new BsonBinaryData(entity.Payload) }
            })));
            Assert.That(operation.Update["$set"].AsBsonDocument.Contains("business_id"), Is.False);
            Assert.That(operation.Update["$set"].AsBsonDocument.Contains("tenant_id"), Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Update_UsesUpdateManyWithoutUpsertAndPassesCancellation(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        using var cancellation = new CancellationTokenSource();
        var entity = new Row { Id = 12, TenantId = Guid.NewGuid(), Name = "updated" };

        if (asynchronous) await client.UpdateAsync(entity, cancellation.Token);
        else client.Update(entity);

        Assert.Multiple(() =>
        {
            Assert.That(fake.Method, Is.EqualTo(asynchronous ? "UpdateManyAsync" : "UpdateMany"));
            Assert.That(fake.Database, Is.EqualTo("analytics"));
            Assert.That(fake.Collection, Is.EqualTo("rows"));
            Assert.That(fake.Filter, Is.EqualTo(MongoClientExtensions.CreateUpdate(entity).Filter));
            Assert.That(fake.Update, Is.EqualTo(MongoClientExtensions.CreateUpdate(entity).Update));
            Assert.That(fake.Options!.IsUpsert, Is.False);
            Assert.That(fake.Token, Is.EqualTo(asynchronous ? cancellation.Token : CancellationToken.None));
            Assert.That(fake.Disposed, Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Update_InvalidArgumentsOrMapping_DoNotAccessDatabase(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Update<Row>(client, null!, asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Update<Row>(null!, new Row(), asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Update(client, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("at least one Key"));
            Assert.That(async () => await Update(client, new UnmappedKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
            Assert.That(async () => await Update(client, new NullKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Update(client, new KeyOnly(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("non-key Column"));
            Assert.That(async () => await Update(client, new InvalidField(), asynchronous),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("top-level field"));
            Assert.That(async () => await Update(client, new InvalidValue(), asynchronous),
                Throws.TypeOf<BsonSerializationException>());
            Assert.That(fake.DatabaseCalls, Is.Zero);
        });
    }

    [Test]
    public void UpdateAsync_PreCanceled_DoesNotSerializeOrAccessDatabase()
    {
        var client = CreateClient(out var fake);
        Assert.That(async () => await client.UpdateAsync(new InvalidValue(), new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.DatabaseCalls, Is.Zero);
    }

    [Test]
    public void CreateUpdate_WritesForeignKeyWithoutTraversingReference()
    {
        var entity = new Order
        {
            Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Customer = new Customer { Id = Guid.NewGuid() }
        };

        var operation = MongoClientExtensions.CreateUpdate(entity);

        Assert.That(operation.Filter, Is.EqualTo(new BsonDocument("_id", Uuid(entity.Id))));
        Assert.That(operation.Update, Is.EqualTo(new BsonDocument("$set",
            new BsonDocument("customer_id", Uuid(entity.CustomerId)))));
        Assert.That(operation.Update.ToString(), Does.Not.Contain(entity.Customer.Id.ToString()));
    }

    [Test]
    public async Task UpdateAsync_AwaitsDriverCompletion()
    {
        var client = CreateClient(out var fake);
        var pending = new TaskCompletionSource<UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.AsyncResult = pending.Task;
        var update = client.UpdateAsync(new Row { Id = 12, TenantId = Guid.NewGuid() });
        try
        {
            Assert.That(update.IsCompleted, Is.False);
            Assert.That(fake.Method, Is.EqualTo("UpdateManyAsync"));
        }
        finally
        {
            pending.SetResult(Result);
        }
        await update.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static readonly UpdateResult Result = new UpdateResult.Acknowledged(1, 1, null);

    private static BsonBinaryData Uuid(Guid value) => new(value, GuidRepresentation.Standard);

    private static Task Update<T>(IMongoClient client, T entity, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return client.UpdateAsync(entity);
        client.Update(entity);
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
        public string? Database { get; private set; }
        public string? Collection { get; private set; }
        public string? Method { get; private set; }
        public BsonDocument? Filter { get; private set; }
        public BsonDocument? Update { get; private set; }
        public UpdateOptions? Options { get; private set; }
        public CancellationToken Token { get; private set; }
        public int DatabaseCalls { get; private set; }
        public bool Disposed { get; private set; }
        public Task<UpdateResult>? AsyncResult { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var state = root ?? this;
            switch (targetMethod!.Name)
            {
                case "GetDatabase":
                    state.DatabaseCalls++;
                    state.Database = (string)args![0]!;
                    var db = DispatchProxy.Create<IMongoDatabase, FakeMongo>();
                    ((FakeMongo)db).root = state;
                    ((FakeMongo)db).database = state.Database;
                    return db;
                case "GetCollection":
                    state.Collection = (string)args![0]!;
                    var coll = DispatchProxy.Create<IMongoCollection<BsonDocument>, FakeMongo>();
                    ((FakeMongo)coll).root = state;
                    ((FakeMongo)coll).database = database;
                    ((FakeMongo)coll).collection = state.Collection;
                    return coll;
                case "UpdateMany":
                case "UpdateManyAsync":
                    state.Method = targetMethod.Name;
                    var renderArgs = new RenderArgs<BsonDocument>(
                        BsonSerializer.LookupSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry);
                    state.Filter = ((FilterDefinition<BsonDocument>)args![0]!).Render(renderArgs);
                    state.Update = ((UpdateDefinition<BsonDocument>)args[1]!).Render(renderArgs).AsBsonDocument;
                    state.Options = (UpdateOptions)args[2]!;
                    state.Token = (CancellationToken)args[3]!;
                    return targetMethod.Name == "UpdateManyAsync" ? state.AsyncResult ?? Task.FromResult(Result) : Result;
                case "Dispose":
                    state.Disposed = true;
                    return null;
                default: throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    [Table("analytics", "rows")]
    public class Row
    {
        [Key, Column("business_id")] public int Id { get; set; }
        [Key, Column("tenant_id")] public Guid TenantId { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("state")] public State State { get; set; }
        [Column("created")] public DateTime Created { get; set; }
        [Column("payload")] public byte[]? Payload { get; set; }
    }

    public enum State { Inactive, Active }
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
        [Key, Column("id")] public string? Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }
    [Table("analytics", "rows")]
    public class KeyOnly { [Key, Column("id")] public int Id { get; set; } }
    [Table("analytics", "rows")]
    public class InvalidField
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("nested.value")] public int Value { get; set; }
    }
    [Table("analytics", "rows")]
    public class InvalidValue
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("payload")] public InvalidPayload Payload { get; set; } = new();
    }
    [BsonSerializer(typeof(InvalidPayloadSerializer))]
    public class InvalidPayload { }
    public class InvalidPayloadSerializer : SerializerBase<InvalidPayload>
    {
        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, InvalidPayload value)
            => throw new BsonSerializationException("invalid payload");
    }
    [Table("analytics", "customers")]
    public class Customer { [Key, Column("_id")] public Guid Id { get; set; } }
    [Table("analytics", "orders")]
    public class Order
    {
        [Key, Column("_id")] public Guid Id { get; set; }
        [Column("customer_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))] public Guid CustomerId { get; set; }
        [Reference(nameof(CustomerId))] public Customer? Customer { get; set; }
    }
}
