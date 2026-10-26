using System.Reflection;

namespace Akeldov.Data;

/// <summary>Validates and prepares a reference graph before any provider commands are sent.</summary>
internal sealed class CreateGraph(StringComparer columnNameComparer, ICreateGraphCommandBuilder builder) : IDisposable
{
    // Track instances, not key values: two different instances remain two inserts.
    private readonly HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> visiting = new(ReferenceEqualityComparer.Instance);
    private readonly List<(object Entity, PropertyInfo Property, object? Value)> changes = [];
    private bool completed;

    internal void Prepare<T>(T entity) where T : class, new() => Visit(typeof(T), entity);

    internal void Complete() => completed = true;

    public void Dispose()
    {
        if (completed) return;
        for (var index = changes.Count - 1; index >= 0; index--)
        {
            var change = changes[index];
            change.Property.SetValue(change.Entity, change.Value);
        }
        changes.Clear();
    }

    private void Visit(Type type, object row)
    {
        if (visiting.Contains(row))
            throw new InvalidOperationException($"Cyclic Reference graph at '{type.FullName}'.");
        if (visited.Contains(row)) return;
        visiting.Add(row);

        var node = CreateMappedNode(type, row, columnNameComparer);
        var referencedForeignKeys = new HashSet<PropertyInfo>();
        foreach (var navigation in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var reference = navigation.GetCustomAttribute<ReferenceAttribute>();
            if (reference is null) continue;
            if (navigation.GetGetMethod() is null || navigation.GetIndexParameters().Length != 0
                || navigation.IsDefined(typeof(ColumnAttribute)))
                throw InvalidReference(navigation, "must have a public getter, must not be an indexer or a Column");

            var foreignKey = node.Columns.SingleOrDefault(column => column.Name == reference.ForeignKeyProperty);
            var relation = foreignKey?.GetCustomAttribute<ForeignKeyAttribute>();
            if (foreignKey is null || relation is null)
                throw InvalidReference(navigation, "must name a local property marked with Column and ForeignKey");
            if (!referencedForeignKeys.Add(foreignKey))
                throw InvalidReference(navigation, "uses a foreign key already assigned to another Reference");

            var targetType = relation.ReferencedType;
            if (!targetType.IsAssignableFrom(navigation.PropertyType))
                throw InvalidReference(navigation, "has a type incompatible with ForeignKey.ReferencedType");

            var key = targetType.GetProperty(relation.ReferencedProperty, BindingFlags.Public | BindingFlags.Instance);
            if (key is null || !key.IsDefined(typeof(KeyAttribute)) || !key.IsDefined(typeof(ColumnAttribute))
                || key.GetGetMethod() is null || key.GetIndexParameters().Length != 0)
                throw InvalidReference(navigation, "must reference a readable property marked with Key and Column");
            if ((Nullable.GetUnderlyingType(foreignKey.PropertyType) ?? foreignKey.PropertyType)
                != (Nullable.GetUnderlyingType(key.PropertyType) ?? key.PropertyType))
                throw InvalidReference(navigation, "has incompatible foreign key and key types");

            var child = navigation.GetValue(row);
            // A null navigation leaves the caller-supplied FK intact (an existing row).
            if (child is null) continue;
            Visit(targetType, child);
            var keyValue = key.GetValue(child)
                ?? throw InvalidReference(navigation, "has a null referenced key");
            changes.Add((row, foreignKey, foreignKey.GetValue(row)));
            foreignKey.SetValue(row, keyValue);
        }

        node.PrepareCommand(builder);
        visiting.Remove(row);
        visited.Add(row);
    }

    private static InvalidOperationException InvalidReference(PropertyInfo property, string reason)
        => new($"Reference '{property.DeclaringType?.FullName}.{property.Name}' {reason}.");

    // Adapt runtime reference types to the existing generic mapper and parameter builder.
    private static ICreateNode CreateMappedNode(Type type, object entity, StringComparer columnNameComparer)
    {
        if (!type.IsClass || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException($"Reference type '{type.FullName}' must be a concrete class with a public parameterless constructor.");
        var node = (ICreateNode)Activator.CreateInstance(typeof(CreateNode<>).MakeGenericType(type))!;
        node.Initialize(entity, columnNameComparer);
        return node;
    }

    private interface ICreateNode
    {
        IEnumerable<PropertyInfo> Columns { get; }
        void Initialize(object entity, StringComparer columnNameComparer);
        void PrepareCommand(ICreateGraphCommandBuilder builder);
    }

    private sealed class CreateNode<T> : ICreateNode where T : class, new()
    {
        private T entity = null!;
        public IEnumerable<PropertyInfo> Columns { get; private set; } = [];

        public void Initialize(object value, StringComparer columnNameComparer)
        {
            entity = (T)value;
            var mapping = TableMapping<T>.Create();
            mapping.ValidateColumnNames(columnNameComparer);
            mapping.ValidateGetters();
            Columns = mapping.Columns.Select(column => column.Property).ToArray();
        }

        public void PrepareCommand(ICreateGraphCommandBuilder builder)
            => builder.Add(entity);
    }
}

internal interface ICreateGraphCommandBuilder
{
    void Add<T>(T entity) where T : class, new();
}
