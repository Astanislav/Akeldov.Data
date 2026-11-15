using System.Reflection;

namespace Akeldov.Data;

internal sealed record CollectionMapping(
    PropertyInfo Property, Type ElementType, TableAttribute Table,
    PropertyInfo ParentKey, string ParentKeyColumn,
    PropertyInfo ForeignKey, string ForeignKeyColumn)
{
    internal static bool IsCollection(Type type) => type != typeof(string)
        && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    internal static CollectionMapping Create(Type parentType, PropertyInfo property, ReferenceAttribute attribute)
    {
        InvalidOperationException Invalid(string reason)
            => new($"Collection '{parentType.FullName}.{property.Name}' {reason}.");

        if (property.GetGetMethod() is null || property.GetSetMethod() is null || property.GetIndexParameters().Length != 0
            || property.IsDefined(typeof(ColumnAttribute)))
            throw Invalid("must have public getter and setter and must not be an indexer or Column");

        var type = property.PropertyType;
        var elementType = type.IsArray && type.GetArrayRank() == 1 ? type.GetElementType()
            : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;
        if (elementType is null) throw Invalid("must be List<T> or a one-dimensional array");
        if (!elementType.IsClass || elementType.IsAbstract || elementType.GetConstructor(Type.EmptyTypes) is null)
            throw Invalid("must contain concrete entities with a public parameterless constructor");
        var table = elementType.GetCustomAttribute<TableAttribute>()
            ?? throw Invalid("must contain entities marked with Table");

        var foreignKey = elementType.GetProperty(attribute.ForeignKeyProperty, BindingFlags.Public | BindingFlags.Instance);
        var relation = foreignKey?.GetCustomAttribute<ForeignKeyAttribute>();
        var foreignColumn = foreignKey?.GetCustomAttribute<ColumnAttribute>();
        if (foreignKey is null || relation is null || foreignColumn is null
            || foreignKey.GetGetMethod() is null || foreignKey.GetSetMethod() is null || foreignKey.GetIndexParameters().Length != 0)
            throw Invalid("must name a readable and writable child property marked with Column and ForeignKey");
        if (relation.ReferencedType != parentType)
            throw Invalid("has a child ForeignKey that references a different parent type");

        var keys = parentType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(key => key.IsDefined(typeof(KeyAttribute))).ToArray();
        if (keys.Length != 1 || keys[0].Name != relation.ReferencedProperty)
            throw Invalid("must reference the parent's single Key; composite keys are not supported");
        var key = keys[0];
        var keyColumn = key.GetCustomAttribute<ColumnAttribute>();
        if (keyColumn is null || key.GetGetMethod() is null || key.GetSetMethod() is null || key.GetIndexParameters().Length != 0)
            throw Invalid("must reference a readable and writable parent Key marked with Column");
        if ((Nullable.GetUnderlyingType(foreignKey.PropertyType) ?? foreignKey.PropertyType)
            != (Nullable.GetUnderlyingType(key.PropertyType) ?? key.PropertyType))
            throw Invalid("has incompatible foreign key and parent key types");

        return new(property, elementType, table, key, keyColumn.Name, foreignKey, foreignColumn.Name);
    }
}
