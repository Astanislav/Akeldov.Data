using Akeldov.Data.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

namespace Akeldov.Data.MongoDb.IntegrationTests;

/// <summary>Requires Docker with Linux containers, or MONGODB_CONNECTION_STRING.</summary>
[TestFixture]
public class MongoClientSelectTests
{
    private MongoDbContainer? container;
    private MongoClient client = null!;
    private static readonly ObjectId FirstId = ObjectId.GenerateNewId();
    private static readonly Guid Uuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private const string DatabaseName = "akeldov_data_integration";

    [OneTimeSetUp]
    public async Task StartDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            container = new MongoDbBuilder("mongo:8.0").Build();
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await container.StartAsync(cancellation.Token);
                connectionString = container.GetConnectionString();
            }
            catch
            {
                await container.DisposeAsync();
                container = null;
                throw;
            }
        }

        client = new MongoClient(connectionString);
        // Dedicated test collections are the only data modified on an externally supplied server.
        var database = client.GetDatabase(DatabaseName);
        await Seed(database, "users",
        [
            new() { { "_id", FirstId }, { "name", "Alice" }, { "score", 10 }, { "other_score", 5 }, { "active", true }, { "extra", 123 } },
            new() { { "_id", ObjectId.GenerateNewId() }, { "name", "Bob" }, { "score", 20 }, { "other_score", 20 }, { "active", false } },
            new() { { "_id", ObjectId.GenerateNewId() }, { "name", BsonNull.Value }, { "score", BsonNull.Value }, { "active", true } },
            new() { { "_id", ObjectId.GenerateNewId() }, { "active", true } }
        ]);
        await Seed(database, "guid_keys", [new() { { "_id", new BsonBinaryData(Uuid, GuidRepresentation.Standard) } }]);
        await Seed(database, "string_keys", [new() { { "_id", "custom-key" } }]);
        await Seed(database, "number_keys", [new() { { "_id", 42 } }]);
        await Seed(database, "case_fields", [new() { { "id", 1 }, { "ID", 2 } }]);
    }

    private static async Task Seed(IMongoDatabase database, string name, BsonDocument[] documents)
    {
        var collection = database.GetCollection<BsonDocument>(name);
        await collection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
        await collection.InsertManyAsync(documents);
    }

    [OneTimeTearDown]
    public async Task StopDatabase()
    {
        client?.Dispose();
        if (container is not null) await container.DisposeAsync();
    }

    [Test]
    public void Select_MapsOnlyColumnsAndPreservesObjectId()
    {
        var row = client.Select<User>(row => row.Id == FirstId).Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.Id, Is.EqualTo(FirstId));
            Assert.That(row.Name, Is.EqualTo("Alice"));
            Assert.That(row.Score, Is.EqualTo(10));
            Assert.That(row.Unmapped, Is.EqualTo("default"));
        });
        Assert.That(client.Select<User>(), Has.Count.EqualTo(4));
    }

    [Test]
    public async Task SelectAsync_UsesCapturedValuesAndBooleanFilters()
    {
        var minimum = 10;
        var rows = await client.SelectAsync<User>(row => row.Score >= minimum && !row.Active);
        Assert.That(rows.Select(row => row.Name), Is.EqualTo(new[] { "Bob" }));
        Assert.That(await client.SelectAsync<User>(), Has.Count.EqualTo(4));
    }

    [Test]
    public void Select_NullChecks_MatchNullAndMissingFields()
    {
        Assert.That(client.Select<User>(row => row.Score == null), Has.Count.EqualTo(2));
        Assert.That(client.Select<User>(row => row.Score != null), Has.Count.EqualTo(2));
        Assert.That(client.Select<User>(row => row.Score > 10), Has.Count.EqualTo(1));
    }

    [Test]
    public void Select_ColumnComparison_IsExecutedByMongoDb()
        => Assert.That(client.Select<User>(row => row.Score > row.OtherScore).Select(row => row.Name), Is.EqualTo(new[] { "Alice" }));

    [Test]
    public void Select_GuidStringAndNumericKeys_RoundTrip()
    {
        Assert.Multiple(() =>
        {
            Assert.That(client.Select<GuidRow>(row => row.Id == Uuid).Single().Id, Is.EqualTo(Uuid));
            Assert.That(client.Select<StringRow>(row => row.Id == "custom-key").Single().Id, Is.EqualTo("custom-key"));
            Assert.That(client.Select<NumberRow>(row => row.Id == 42).Single().Id, Is.EqualTo(42));
        });
    }

    [Test]
    public void Select_WithoutIdProjection_PreservesCase()
    {
        var row = client.Select<CaseRow>(row => row.Id == 1 && row.OtherId == 2).Single();
        Assert.That(row.OtherId, Is.EqualTo(2));
    }

    [Test]
    public void SelectAsync_CanceledToken_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.That(async () => await client.SelectAsync<User>(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Table(DatabaseName, "users")]
    public class User
    {
        [Column("_id")] public ObjectId Id { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("other_score")] public int? OtherScore { get; set; }
        [Column("active")] public bool Active { get; set; }
        public string Unmapped { get; set; } = "default";
    }
    [Table(DatabaseName, "guid_keys")]
    public class GuidRow { [Column("_id")] public Guid Id { get; set; } }
    [Table(DatabaseName, "string_keys")]
    public class StringRow { [Column("_id")] public string Id { get; set; } = ""; }
    [Table(DatabaseName, "number_keys")]
    public class NumberRow { [Column("_id")] public int Id { get; set; } }
    [Table(DatabaseName, "case_fields")]
    public class CaseRow
    {
        [Column("id")] public int Id { get; set; }
        [Column("ID")] public int OtherId { get; set; }
    }
}
