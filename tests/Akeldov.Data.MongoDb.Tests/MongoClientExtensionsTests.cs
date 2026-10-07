using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NUnit.Framework;
using User = Akeldov.Data.MongoDb.Tests.MongoSelectQueryTests.User;

namespace Akeldov.Data.MongoDb.Tests;

[TestFixture]
public class MongoClientExtensionsTests
{
    [Test]
    public void Select_MapsMultipleBatchesAndDisposesCursor()
    {
        var client = CreateClient(out var fake);
        var rows = client.Select<User>(row => row.Score >= 10);
        Assert.Multiple(() =>
        {
            Assert.That(fake.DatabaseName, Is.EqualTo("analytics"));
            Assert.That(fake.CollectionName, Is.EqualTo("users"));
            Assert.That(fake.Filter, Is.EqualTo(BsonDocument.Parse("{score: {$gte: 10}}")));
            Assert.That(fake.Projection, Is.EqualTo(BsonDocument.Parse("{_id: 1, name: 1, score: 1, active: 1}")));
            Assert.That(rows.Select(row => row.Name), Is.EqualTo(new[] { "Alice", "Bob" }));
            Assert.That(fake.Cursor.Disposed, Is.True);
            Assert.That(fake.Disposed, Is.False);
        });
    }

    [Test]
    public async Task SelectAsync_PassesCancellationToFindAndCursor()
    {
        var client = CreateClient(out var fake);
        using var cancellation = new CancellationTokenSource();
        var rows = await client.SelectAsync<User>(row => row.Active, cancellation.Token);
        Assert.Multiple(() =>
        {
            Assert.That(fake.AsyncCalls, Is.EqualTo(1));
            Assert.That(fake.Token, Is.EqualTo(cancellation.Token));
            Assert.That(fake.Cursor.Token, Is.EqualTo(cancellation.Token));
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(fake.Cursor.Disposed, Is.True);
        });
    }

    [Test]
    public async Task SelectAsync_WithoutPredicate_SendsEmptyFilter()
    {
        var client = CreateClient(out var fake);
        await client.SelectAsync<User>();
        Assert.That(fake.Filter, Is.EqualTo(new BsonDocument()));
    }

    [Test]
    public void Select_EmptyCollection_ReturnsEmptyList()
    {
        var client = CreateClient(out var fake);
        fake.Cursor.Batches = [];
        Assert.That(client.Select<User>(), Is.Empty);
        Assert.That(fake.Cursor.Disposed, Is.True);
    }

    [Test]
    public void InvalidArgumentsMappingOrPredicate_ThrowBeforeAccessingDatabase()
    {
        var client = CreateClient(out var fake);
        Assert.Multiple(() =>
        {
            Assert.That(() => client.Select<User>((Expression<Func<User, bool>>)null!), Throws.ArgumentNullException);
            Assert.That(() => client.Select<MongoSelectQueryTests.Unmapped>(), Throws.InvalidOperationException);
            Assert.That(() => client.Select<User>(row => MongoSelectQueryTests.Unsupported(row.Name)), Throws.InstanceOf<NotSupportedException>());
            Assert.That(async () => await client.SelectAsync<User>(row => MongoSelectQueryTests.Unsupported(row.Name)),
                Throws.InstanceOf<NotSupportedException>());
            Assert.That(async () => await client.SelectAsync<User>((Expression<Func<User, bool>>)null!), Throws.ArgumentNullException);
            Assert.That(fake.DatabaseCalls, Is.Zero);
        });
    }

    [Test]
    public void NullClient_Throws()
    {
        IMongoClient client = null!;
        Assert.Multiple(() =>
        {
            Assert.That(() => client.Select<User>(), Throws.ArgumentNullException);
            Assert.That(() => client.Select<User>(row => true), Throws.ArgumentNullException);
            Assert.That(async () => await client.SelectAsync<User>(), Throws.ArgumentNullException);
        });
    }

    [Test]
    public void SelectAsync_CanceledToken_DoesNotAccessDatabase()
    {
        var client = CreateClient(out var fake);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.That(async () => await client.SelectAsync<User>(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.DatabaseCalls, Is.Zero);
    }

    [Test]
    public void SelectAsync_CancellationDuringReading_DisposesCursor()
    {
        var client = CreateClient(out var fake);
        using var cancellation = new CancellationTokenSource();
        fake.Cursor.OnCurrent = cancellation.Cancel;
        Assert.That(async () => await client.SelectAsync<User>(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.Cursor.Disposed, Is.True);
    }

    [Test]
    public void Select_MappingFailure_DisposesCursor()
    {
        var client = CreateClient(out var fake);
        fake.Cursor.Batches = [[new BsonDocument("active", BsonNull.Value)]];
        Assert.That(() => client.Select<User>(), Throws.InvalidOperationException);
        Assert.That(fake.Cursor.Disposed, Is.True);
    }

    [Test]
    public void SelectAsync_CursorFailure_DisposesCursor()
    {
        var client = CreateClient(out var fake);
        fake.Cursor.OnMove = () => throw new InvalidOperationException("cursor failure");
        Assert.That(async () => await client.SelectAsync<User>(), Throws.InvalidOperationException.With.Message.EqualTo("cursor failure"));
        Assert.That(fake.Cursor.Disposed, Is.True);
    }

    private static IMongoClient CreateClient(out FakeMongo fake)
    {
        var client = DispatchProxy.Create<IMongoClient, FakeMongo>();
        fake = (FakeMongo)client;
        var database = DispatchProxy.Create<IMongoDatabase, FakeMongo>();
        var collection = DispatchProxy.Create<IMongoCollection<BsonDocument>, FakeMongo>();
        fake.Database = database;
        fake.Collection = collection;
        ((FakeMongo)database).Root = fake;
        ((FakeMongo)collection).Root = fake;
        return client;
    }

    public class FakeMongo : DispatchProxy
    {
        public FakeMongo? Root { get; set; }
        public IMongoDatabase Database { get; set; } = null!;
        public IMongoCollection<BsonDocument> Collection { get; set; } = null!;
        public string? DatabaseName { get; private set; }
        public string? CollectionName { get; private set; }
        public BsonDocument? Filter { get; private set; }
        public BsonDocument? Projection { get; private set; }
        public int DatabaseCalls { get; private set; }
        public int AsyncCalls { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool Disposed { get; private set; }
        public FakeCursor Cursor { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var root = Root ?? this;
            switch (targetMethod!.Name)
            {
                case "GetDatabase":
                    root.DatabaseCalls++;
                    root.DatabaseName = (string)args![0]!;
                    return root.Database;
                case "GetCollection":
                    root.CollectionName = (string)args![0]!;
                    return root.Collection;
                case "FindSync":
                case "FindAsync":
                    root.Filter = ((FilterDefinition<BsonDocument>)args![0]!).Render(
                        new RenderArgs<BsonDocument>(BsonSerializer.LookupSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
                    root.Projection = ((FindOptions<BsonDocument, BsonDocument>)args[1]!).Projection.Render(
                        new RenderArgs<BsonDocument>(BsonSerializer.LookupSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry)).Document;
                    root.Token = (CancellationToken)args[2]!;
                    if (targetMethod.Name == "FindAsync")
                    {
                        root.AsyncCalls++;
                        return Task.FromResult<IAsyncCursor<BsonDocument>>(root.Cursor);
                    }
                    return root.Cursor;
                case "Dispose":
                    root.Disposed = true;
                    return null;
                default: throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    public sealed class FakeCursor : IAsyncCursor<BsonDocument>
    {
        private int index = -1;
        public BsonDocument[][] Batches { get; set; } =
        [
            [new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "Alice" }, { "score", 10 } }],
            [new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "Bob" }, { "score", 20 } }]
        ];
        public Action? OnCurrent { get; set; }
        public Action? OnMove { get; set; }
        public CancellationToken Token { get; private set; }
        public bool Disposed { get; private set; }
        public IEnumerable<BsonDocument> Current
        {
            get { OnCurrent?.Invoke(); return Batches[index]; }
        }
        public bool MoveNext(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            OnMove?.Invoke();
            return ++index < Batches.Length;
        }
        public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(MoveNext(cancellationToken));
        public void Dispose() => Disposed = true;
    }
}
