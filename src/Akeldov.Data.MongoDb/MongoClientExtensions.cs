using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Akeldov.Data.MongoDb;

/// <summary>Reads and inserts mapped models in MongoDB collections.</summary>
/// <remarks>Table.Schema specifies the database and Table.Name the collection. The caller owns the client.</remarks>
public static class MongoClientExtensions
{
    /// <summary>Inserts one document using properties marked with Column.</summary>
    /// <remarks>Mapped IDs are sent as supplied; generated IDs are not written back to the entity.</remarks>
    public static void Create<T>(this IMongoClient client, T entity) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(entity);
        var (mapping, document) = CreateDocument(entity);
        client.GetDatabase(mapping.Table.Schema).GetCollection<BsonDocument>(mapping.Table.Name).InsertOne(document);
    }

    /// <summary>Asynchronously inserts one mapped document.</summary>
    public static async Task CreateAsync<T>(this IMongoClient client, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();
        var (mapping, document) = CreateDocument(entity);
        await client.GetDatabase(mapping.Table.Schema).GetCollection<BsonDocument>(mapping.Table.Name)
            .InsertOneAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static (TableMapping<T> Mapping, BsonDocument Document) CreateDocument<T>(T entity) where T : class, new()
    {
        var mapping = TableMapping<T>.Create();
        mapping.ValidateGetters();
        var serializer = new MongoDocumentSerializer<T>(mapping);
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        serializer.Serialize(BsonSerializationContext.CreateRoot(writer), new BsonSerializationArgs { NominalType = typeof(T) }, entity);
        return (mapping, document);
    }

    /// <summary>Reads all documents, populating properties marked with Column.</summary>
    public static List<T> Select<T>(this IMongoClient client) where T : class, new()
        => Execute<T>(client);

    /// <summary>Reads documents matching an expression translated by the MongoDB driver.</summary>
    /// <remarks>Unsupported expressions throw before a query is sent.</remarks>
    public static List<T> Select<T>(this IMongoClient client, Expression<Func<T, bool>> predicate)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Execute(client, predicate);
    }

    /// <summary>Asynchronously reads all mapped documents.</summary>
    public static Task<List<T>> SelectAsync<T>(this IMongoClient client, CancellationToken cancellationToken = default)
        where T : class, new()
        => ExecuteAsync<T>(client, null, cancellationToken);

    /// <summary>Asynchronously reads documents matching a translated expression.</summary>
    public static Task<List<T>> SelectAsync<T>(this IMongoClient client, Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return ExecuteAsync(client, predicate, cancellationToken);
    }

    private static List<T> Execute<T>(IMongoClient client, Expression<Func<T, bool>>? predicate = null)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(client);
        var query = new MongoSelectQuery<T>(predicate);
        var collection = client.GetDatabase(query.Database).GetCollection<BsonDocument>(query.Collection);
        using var cursor = collection.FindSync<BsonDocument>(query.Filter,
            new FindOptions<BsonDocument, BsonDocument> { Projection = query.Projection });
        var rows = new List<T>();
        while (cursor.MoveNext())
        {
            foreach (var document in cursor.Current) rows.Add(query.Read(document));
        }

        return rows;
    }

    private static async Task<List<T>> ExecuteAsync<T>(IMongoClient client, Expression<Func<T, bool>>? predicate,
        CancellationToken cancellationToken) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(client);
        cancellationToken.ThrowIfCancellationRequested();
        var query = new MongoSelectQuery<T>(predicate);
        var collection = client.GetDatabase(query.Database).GetCollection<BsonDocument>(query.Collection);
        using var cursor = await collection.FindAsync<BsonDocument>(query.Filter,
            new FindOptions<BsonDocument, BsonDocument> { Projection = query.Projection }, cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<T>();
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in cursor.Current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(query.Read(document));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return rows;
    }
}
