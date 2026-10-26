using System.Data;
using System.Data.Common;
using System.Reflection;

namespace Akeldov.Data;

internal static class CreateGraphExecutor
{
    internal static void Create<T>(DbConnection connection, ISqlDialect dialect, T entity) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entity);

        var commands = new List<DbCommand>();
        // Track instances, not key values: two different instances remain two inserts.
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var visiting = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var changes = new List<(object Entity, PropertyInfo Property, object? Value)>();
        var shouldClose = connection.State == ConnectionState.Closed;
        var succeeded = false;
        try
        {
            Visit(typeof(T), entity);
            if (shouldClose) connection.Open();
            using var transaction = commands.Count > 1 ? connection.BeginTransaction() : null;
            try
            {
                foreach (var command in commands)
                {
                    command.Transaction = transaction;
                    command.ExecuteNonQuery();
                }
                transaction?.Commit();
                succeeded = true;
            }
            catch
            {
                // Preserve the insert exception even if the connection has been lost.
                try { transaction?.Rollback(); }
                catch { }
                throw;
            }
        }
        finally
        {
            foreach (var command in commands) command.Dispose();
            if (!succeeded)
            {
                for (var index = changes.Count - 1; index >= 0; index--)
                {
                    var change = changes[index];
                    change.Property.SetValue(change.Entity, change.Value);
                }
            }
            if (shouldClose && connection.State != ConnectionState.Closed) connection.Close();
        }

        void Visit(Type type, object row)
        {
            if (visiting.Contains(row))
                throw new InvalidOperationException($"Cyclic Reference graph at '{type.FullName}'.");
            if (visited.Contains(row)) return;
            visiting.Add(row);

            var node = CreateMappedNode(type, row, dialect);
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

            commands.Add(node.CreateCommand(connection, dialect));
            visiting.Remove(row);
            visited.Add(row);
        }
    }

    private static InvalidOperationException InvalidReference(PropertyInfo property, string reason)
        => new($"Reference '{property.DeclaringType?.FullName}.{property.Name}' {reason}.");

    // Adapt runtime reference types to the existing generic mapper and parameter builder.
    private static ICreateNode CreateMappedNode(Type type, object entity, ISqlDialect dialect)
    {
        if (!type.IsClass || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException($"Reference type '{type.FullName}' must be a concrete class with a public parameterless constructor.");
        var node = (ICreateNode)Activator.CreateInstance(typeof(CreateNode<>).MakeGenericType(type))!;
        node.Initialize(entity, dialect);
        return node;
    }

    private interface ICreateNode
    {
        IEnumerable<PropertyInfo> Columns { get; }
        void Initialize(object entity, ISqlDialect dialect);
        DbCommand CreateCommand(DbConnection connection, ISqlDialect dialect);
    }

    private sealed class CreateNode<T> : ICreateNode where T : class, new()
    {
        private T entity = null!;
        public IEnumerable<PropertyInfo> Columns { get; private set; } = [];

        public void Initialize(object value, ISqlDialect dialect)
        {
            entity = (T)value;
            var mapping = TableMapping<T>.Create();
            mapping.ValidateColumnNames(dialect.ColumnNameComparer);
            mapping.ValidateGetters();
            Columns = mapping.Columns.Select(column => column.Property).ToArray();
        }

        public DbCommand CreateCommand(DbConnection connection, ISqlDialect dialect)
            => CreateExecutor.CreateCommand(connection, dialect, entity);
    }
}
