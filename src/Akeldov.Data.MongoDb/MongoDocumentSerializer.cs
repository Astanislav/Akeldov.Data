using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Akeldov.Data.MongoDb;

// This serializer is local to a select: it never changes the driver's global class maps.
internal sealed class MongoDocumentSerializer<T> : SerializerBase<T>, IBsonDocumentSerializer
    where T : class, new()
{
    private readonly (PropertyInfo Property, string Name, IBsonSerializer Serializer)[] columns;

    internal MongoDocumentSerializer(TableMapping<T> mapping)
    {
        columns = mapping.Columns.Select(column =>
        {
            if (column.Name.Contains('.') || column.Name.StartsWith('$') || column.Name.Contains('\0'))
            {
                throw new NotSupportedException($"MongoDB column '{column.Name}' must be a top-level field without dots, a leading '$', or null characters.");
            }

            return (column.Property, column.Name, CreateSerializer(column.Property.PropertyType));
        }).ToArray();
    }

    public bool TryGetMemberSerializationInfo(string memberName, out BsonSerializationInfo serializationInfo)
    {
        foreach (var column in columns)
        {
            if (column.Property.Name == memberName)
            {
                serializationInfo = new BsonSerializationInfo(column.Name, column.Serializer, column.Property.PropertyType);
                return true;
            }
        }

        serializationInfo = null!;
        return false;
    }

    public override T Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var row = new T();
        reader.ReadStartDocument();
        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            var name = reader.ReadName();
            var column = Array.Find(columns, column => column.Name == name);
            if (column.Property is null)
            {
                reader.SkipValue();
                continue;
            }

            if (reader.GetCurrentBsonType() == BsonType.Null)
            {
                if (column.Property.PropertyType.IsValueType && Nullable.GetUnderlyingType(column.Property.PropertyType) is null)
                {
                    throw new InvalidOperationException($"Column '{name}' is NULL but property type '{column.Property.PropertyType}' is not nullable.");
                }

                reader.ReadNull();
                column.Property.SetValue(row, null);
            }
            else
            {
                var value = column.Serializer.Deserialize(context,
                    new BsonDeserializationArgs { NominalType = column.Property.PropertyType });
                column.Property.SetValue(row, value);
            }
        }

        reader.ReadEndDocument();
        return row;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, T value)
    {
        context.Writer.WriteStartDocument();
        foreach (var column in columns)
        {
            context.Writer.WriteName(column.Name);
            column.Serializer.Serialize(context, new BsonSerializationArgs { NominalType = column.Property.PropertyType },
                column.Property.GetValue(value));
        }

        context.Writer.WriteEndDocument();
    }

    private static IBsonSerializer CreateSerializer(Type type)
    {
        if (type == typeof(Guid)) return new GuidSerializer(GuidRepresentation.Standard);
        if (type == typeof(Guid?)) return new NullableSerializer<Guid>(new GuidSerializer(GuidRepresentation.Standard));
        if (type == typeof(DateTime)) return new DateTimeSerializer(DateTimeKind.Utc);
        if (type == typeof(DateTime?)) return new NullableSerializer<DateTime>(new DateTimeSerializer(DateTimeKind.Utc));
        return BsonSerializer.LookupSerializer(type);
    }
}
