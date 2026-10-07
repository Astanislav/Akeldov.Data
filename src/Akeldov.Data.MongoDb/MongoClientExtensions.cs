using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Akeldov.Data.MongoDb;

/// <summary>Reads mapped models from MongoDB collections.</summary>
/// <remarks>Table.Schema specifies the database and Table.Name the collection. The caller owns the client.</remarks>
public static class MongoClientExtensions
{
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
