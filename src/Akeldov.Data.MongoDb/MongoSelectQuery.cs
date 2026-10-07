using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Akeldov.Data.MongoDb;

internal sealed class MongoSelectQuery<T> where T : class, new()
{
    private readonly MongoDocumentSerializer<T> serializer;

    internal MongoSelectQuery(Expression<Func<T, bool>>? predicate = null)
    {
        var mapping = TableMapping<T>.Create();
        serializer = new MongoDocumentSerializer<T>(mapping);
        Database = mapping.Table.Schema;
        Collection = mapping.Table.Name;
        Filter = predicate is null
            ? new BsonDocument()
            : new ExpressionFilterDefinition<T>(predicate).Render(new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry));
        Projection = new BsonDocument(mapping.ColumnNames.Select(name => new BsonElement(name, 1)));
        if (!Projection.Contains("_id")) Projection.Add("_id", 0);
    }

    internal string Database { get; }
    internal string Collection { get; }
    internal BsonDocument Filter { get; }
    internal BsonDocument Projection { get; }

    internal T Read(BsonDocument document)
    {
        using var reader = new BsonDocumentReader(document);
        return serializer.Deserialize(BsonDeserializationContext.CreateRoot(reader));
    }
}
