using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using NUnit.Framework;

namespace Akeldov.Data.MongoDb.Tests;

[TestFixture]
public class DeleteTests
{
    [Test]
    public void CreateDelete_UsesCompositeKeysAndIgnoresNonKeyValuesAndReferences()
    {
        var row = new Row { Id = 12, TenantId = Guid.NewGuid() };
        row.Child = row;
        var operation = MongoClientExtensions.CreateDelete(row);
        Assert.That(operation.Database, Is.EqualTo("analytics"));
        Assert.That(operation.Collection, Is.EqualTo("rows"));
        Assert.That(operation.Filter, Is.EqualTo(new BsonDocument
        {
            { "business_id", Eq(12) },
            { "tenant_id", Eq(new BsonBinaryData(row.TenantId, GuidRepresentation.Standard)) }
        }));
        Assert.That(row.Child, Is.SameAs(row));
        Assert.That(row.Id, Is.EqualTo(12));
    }

    [Test]
    public void CreateDelete_PreservesKeyBsonTypesAndTreatsOperatorDocumentsAsData()
    {
        CheckKey(ObjectId.Empty, ObjectId.Empty);
        CheckKey(Guid.Empty, new BsonBinaryData(Guid.Empty, GuidRepresentation.Standard));
        CheckKey<Guid?>(Guid.Empty, new BsonBinaryData(Guid.Empty, GuidRepresentation.Standard));
        CheckKey(DateTime.UnixEpoch, new BsonDateTime(DateTime.UnixEpoch));
        CheckKey(Status.Active, new BsonInt32(1));
        CheckKey(0, new BsonInt32(0));
        CheckKey("{'$ne': null}", new BsonString("{'$ne': null}"));
        CheckKey(new byte[] { 0, 255 }, new BsonBinaryData(new byte[] { 0, 255 }));
        CheckKey(new BsonDocument("$ne", BsonNull.Value), new BsonDocument("$ne", BsonNull.Value));
        CheckKey(new BsonRegularExpression(".*"), new BsonRegularExpression(".*"));
    }

    private static void CheckKey<T>(T value, BsonValue expected)
    {
        var operation = MongoClientExtensions.CreateDelete(new KeyOnly<T> { Id = value });
        Assert.That(operation.Filter, Is.EqualTo(new BsonDocument("_id", Eq(expected))));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_UsesDeleteManyAndPassesCancellationWithoutDisposingClient(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        using var cancellation = new CancellationTokenSource();
        var row = new Row { Id = 12, TenantId = Guid.NewGuid() };
        if (asynchronous) await client.DeleteAsync(row, cancellation.Token);
        else client.Delete(row);
        Assert.Multiple(() =>
        {
            Assert.That(fake.Method, Is.EqualTo(asynchronous ? "DeleteManyAsync" : "DeleteMany"));
            Assert.That(fake.Database, Is.EqualTo("analytics"));
            Assert.That(fake.Collection, Is.EqualTo("rows"));
            Assert.That(fake.Calls, Is.EqualTo(1));
            Assert.That(fake.Filter, Is.EqualTo(MongoClientExtensions.CreateDelete(row).Filter));
            Assert.That(fake.Token, Is.EqualTo(asynchronous ? cancellation.Token : CancellationToken.None));
            Assert.That(fake.Disposed, Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Delete_InvalidArgumentsOrMapping_DoNotAccessDatabase(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Delete<Row>(client, null!, asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Delete(null!, new Row(), asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Delete(client, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("at least one Key"));
            Assert.That(async () => await Delete(client, new UnmappedKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
            Assert.That(async () => await Delete(client, new KeyOnly<string>(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Delete(client, new KeyOnly<BsonValue> { Id = BsonNull.Value }, asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("BSON null"));
            Assert.That(async () => await Delete(client, new WriteOnlyKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("public getter"));
            Assert.That(async () => await Delete(client, new DuplicateColumn(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("mapped more than once"));
            Assert.That(async () => await Delete(client, new InvalidField(), asynchronous),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("top-level field"));
            Assert.That(async () => await Delete(client, new KeyOnly<InvalidPayload> { Id = new() }, asynchronous),
                Throws.TypeOf<BsonSerializationException>());
            Assert.That(fake.DatabaseCalls, Is.Zero);
        });
    }

    [Test]
    public void DeleteAsync_PreCanceled_DoesNotSerializeOrAccessDatabase()
    {
        var client = CreateClient(out var fake);
        Assert.That(async () => await client.DeleteAsync(new KeyOnly<InvalidPayload> { Id = new() }, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.DatabaseCalls, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Delete_DriverFailure_PropagatesWithoutDisposingClient(bool asynchronous)
    {
        var client = CreateClient(out var fake);
        fake.Fail = true;
        Assert.That(async () => await Delete(client, new KeyOnly<int>(), asynchronous),
            Throws.InvalidOperationException.With.Message.EqualTo("delete failed"));
        Assert.That(fake.Calls, Is.EqualTo(1));
        Assert.That(fake.Disposed, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeleteAsync_AwaitsDriverCompletionAndPropagatesCancellation(bool cancel)
    {
        var client = CreateClient(out var fake);
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<DeleteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.AsyncResult = pending.Task;
        var delete = client.DeleteAsync(new KeyOnly<int>(), cancellation.Token);
        try { Assert.That(delete.IsCompleted, Is.False); }
        finally
        {
            if (cancel) { cancellation.Cancel(); pending.SetCanceled(cancellation.Token); }
            else pending.SetResult(Result);
        }
        if (cancel)
            Assert.That(async () => await delete.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
        else await delete.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fake.Token, Is.EqualTo(cancellation.Token));
        Assert.That(fake.Disposed, Is.False);
    }

    // Zero matches are a successful operation.
    private static readonly DeleteResult Result = new DeleteResult.Acknowledged(0);
    private static BsonDocument Eq(BsonValue value) => new("$eq", value);
    private static Task Delete<T>(IMongoClient client, T row, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return client.DeleteAsync(row);
        client.Delete(row);
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
        public string? Database { get; private set; }
        public string? Collection { get; private set; }
        public string? Method { get; private set; }
        public BsonDocument? Filter { get; private set; }
        public CancellationToken Token { get; private set; }
        public int DatabaseCalls { get; private set; }
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public bool Fail { get; set; }
        public Task<DeleteResult>? AsyncResult { get; set; }
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
                    return db;
                case "GetCollection":
                    state.Collection = (string)args![0]!;
                    var collection = DispatchProxy.Create<IMongoCollection<BsonDocument>, FakeMongo>();
                    ((FakeMongo)collection).root = state;
                    return collection;
                case "DeleteMany":
                case "DeleteManyAsync":
                    state.Calls++;
                    state.Method = targetMethod.Name;
                    state.Filter = ((FilterDefinition<BsonDocument>)args![0]!).Render(new RenderArgs<BsonDocument>(
                        BsonSerializer.LookupSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
                    state.Token = (CancellationToken)args[^1]!;
                    if (state.Fail) throw new InvalidOperationException("delete failed");
                    return targetMethod.Name == "DeleteManyAsync" ? state.AsyncResult ?? Task.FromResult(Result) : Result;
                case "Dispose": state.Disposed = true; return null;
                default: throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    [Table("analytics", "rows")]
    public class Row
    {
        [Key, Column("business_id")] public int Id { get; set; }
        [Key, Column("tenant_id")] public Guid TenantId { get; set; }
        [Column("ignored")] public InvalidPayload Ignored { get => throw new InvalidOperationException(); set { } }
        [Reference(nameof(Id))] public Row? Child { get; set; }
    }
    public enum Status { Active = 1 }
    [Table("analytics", "rows")]
    public class KeyOnly<T> { [Key, Column("_id"), BsonElement("ignored_name")] public T? Id { get; set; } }
    [Table("analytics", "rows")]
    public class MissingKey { [Column("value")] public int Value { get; set; } }
    [Table("analytics", "rows")]
    public class UnmappedKey
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key] public int Tenant { get; set; }
    }
    [Table("analytics", "rows")]
    public class WriteOnlyKey { [Key, Column("id")] public int Id { set { } } }
    [Table("analytics", "rows")]
    public class DuplicateColumn
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("id")] public int Other { get; set; }
    }
    [Table("analytics", "rows")]
    public class InvalidField { [Key, Column("$where")] public int Id { get; set; } }
    [BsonSerializer(typeof(InvalidPayloadSerializer))]
    public class InvalidPayload { }
    public class InvalidPayloadSerializer : SerializerBase<InvalidPayload>
    {
        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, InvalidPayload value)
            => throw new BsonSerializationException("invalid payload");
    }
}
