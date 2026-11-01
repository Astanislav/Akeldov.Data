using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Akeldov.Data.MongoDb;

/// <summary>Reads, inserts, and updates mapped models in MongoDB collections.</summary>
/// <remarks>Table.Schema specifies the database and Table.Name the collection. The caller owns the client.</remarks>
public static class MongoClientExtensions
{
    /// <summary>Inserts a document and its Reference entities, dependencies first.</summary>
    /// <remarks>Caller-supplied Key values are copied to local ForeignKey properties.
    /// Inserts are not transactional: earlier documents remain on failure, but changed foreign keys in memory are restored.
    /// Mapped IDs are sent as supplied; generated IDs are not written back to the entity.</remarks>
    public static void Create<T>(this IMongoClient client, T entity) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(entity);
        var builder = new DocumentBuilder();
        using var graph = new CreateGraph(StringComparer.Ordinal, builder);
        graph.Prepare(entity);
        foreach (var (database, collection, document) in builder.Documents)
            client.GetDatabase(database).GetCollection<BsonDocument>(collection).InsertOne(document);
        graph.Complete();
    }

    /// <summary>Asynchronously inserts a document and its Reference entities, dependencies first.</summary>
    /// <remarks>Cancellation is passed to the driver and checked before each insert.
    /// Earlier documents remain on failure or cancellation, but changed foreign keys in memory are restored.</remarks>
    public static async Task CreateAsync<T>(this IMongoClient client, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new DocumentBuilder();
        using var graph = new CreateGraph(StringComparer.Ordinal, builder);
        graph.Prepare(entity);
        foreach (var (database, collection, document) in builder.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.GetDatabase(database).GetCollection<BsonDocument>(collection)
                .InsertOneAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        graph.Complete();
    }

    /// <summary>Updates non-key Column fields on all documents matching every Key field.</summary>
    /// <remarks>Uses $set without upsert. Keys must be mapped and non-null. Reference entities are not updated.
    /// No matching document is not treated as an error. The caller owns the client.</remarks>
    public static void Update<T>(this IMongoClient client, T entity) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(client);
        var operation = CreateUpdate(entity);
        client.GetDatabase(operation.Database).GetCollection<BsonDocument>(operation.Collection)
            .UpdateMany(operation.Filter, operation.Update, new UpdateOptions { IsUpsert = false });
    }

    /// <summary>Asynchronously updates mapped fields on all documents matching every Key field.</summary>
    /// <remarks>Uses the same mapping rules as Update and passes cancellation to the MongoDB driver.</remarks>
    public static async Task UpdateAsync<T>(this IMongoClient client, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();
        var operation = CreateUpdate(entity);
        cancellationToken.ThrowIfCancellationRequested();
        await client.GetDatabase(operation.Database).GetCollection<BsonDocument>(operation.Collection)
            .UpdateManyAsync(operation.Filter, operation.Update, new UpdateOptions { IsUpsert = false }, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class DocumentBuilder : ICreateGraphCommandBuilder
    {
        public List<(string Database, string Collection, BsonDocument Document)> Documents { get; } = [];

        public void Add<T>(T entity) where T : class, new()
        {
            var (mapping, document) = CreateDocument(entity);
            Documents.Add((mapping.Table.Schema, mapping.Table.Name, document));
        }
    }

    private static (TableMapping<T> Mapping, BsonDocument Document) CreateDocument<T>(T entity) where T : class, new()
    {
        var mapping = TableMapping<T>.Create();
        mapping.ValidateGetters();
        return (mapping, Serialize(mapping, entity));
    }

    internal static MongoUpdateOperation CreateUpdate<T>(T entity) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(entity);
        var mapping = TableMapping<T>.Create();
        mapping.ValidateGetters();
        var columns = mapping.Columns.ToArray();
        var keyProperties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.IsDefined(typeof(KeyAttribute))).ToArray();
        if (keyProperties.Length == 0)
            throw new InvalidOperationException($"Type '{typeof(T).FullName}' must have at least one Key property to update a document.");
        foreach (var property in keyProperties)
        {
            if (!property.IsDefined(typeof(ColumnAttribute)))
                throw new InvalidOperationException($"Key property '{typeof(T).FullName}.{property.Name}' must have a Column attribute.");
        }

        var keys = columns.Where(column => column.Property.IsDefined(typeof(KeyAttribute))).ToArray();
        var assignments = columns.Where(column => !column.Property.IsDefined(typeof(KeyAttribute))).ToArray();
        if (assignments.Length == 0)
            throw new InvalidOperationException($"Type '{typeof(T).FullName}' must have at least one non-key Column to update.");
        foreach (var key in keys)
        {
            if (key.Property.GetValue(entity) is null)
                throw new InvalidOperationException($"Key property '{typeof(T).FullName}.{key.Property.Name}' must not be null when updating a document.");
        }

        var document = Serialize(mapping, entity);
        var filter = new BsonDocument(keys.Select(key =>
            new BsonElement(key.Name, document[key.Name].DeepClone())));
        var fields = new BsonDocument(assignments.Select(column =>
            new BsonElement(column.Name, document[column.Name].DeepClone())));
        return new MongoUpdateOperation(mapping.Table.Schema, mapping.Table.Name, filter,
            new BsonDocument("$set", fields));
    }

    private static BsonDocument Serialize<T>(TableMapping<T> mapping, T entity) where T : class, new()
    {
        var serializer = new MongoDocumentSerializer<T>(mapping);
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        serializer.Serialize(BsonSerializationContext.CreateRoot(writer), new BsonSerializationArgs { NominalType = typeof(T) }, entity);
        return document;
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

internal sealed record MongoUpdateOperation(
    string Database, string Collection, BsonDocument Filter, BsonDocument Update);
