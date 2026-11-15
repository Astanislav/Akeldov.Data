using System.Data.Common;

namespace Akeldov.Data;

internal interface ICollectionLoader
{
    void Initialize(CollectionMapping relation, ISqlDialect dialect);
    void Load(DbConnection connection, object[] parents);
    Task LoadAsync(DbConnection connection, object[] parents, CancellationToken token);
}

internal static class CollectionLoader
{
    internal static ICollectionLoader[] Prepare<T>(TableMapping<T> mapping, ISqlDialect dialect) where T : class, new()
    {
        if (!dialect.LoadsReferenceCollections) return [];
        return mapping.Collections.Select(relation =>
        {
            var loader = (ICollectionLoader)Activator.CreateInstance(typeof(Loader<>).MakeGenericType(relation.ElementType))!;
            loader.Initialize(relation, dialect);
            return loader;
        }).ToArray();
    }

    private sealed class Loader<TChild> : ICollectionLoader where TChild : class, new()
    {
        private CollectionMapping relation = null!;
        private ISqlDialect dialect = null!;
        private TableMapping<TChild> mapping = null!;
        private string selectSql = "";

        public void Initialize(CollectionMapping relation, ISqlDialect dialect)
        {
            this.relation = relation;
            this.dialect = dialect;
            mapping = TableMapping<TChild>.Create();
            selectSql = mapping.CreateSelectSql(dialect);
        }

        private object[] Keys(object[] parents) => parents.Select(parent => relation.ParentKey.GetValue(parent))
            .Where(key => key is not null).Cast<object>().Select(SqlWriteValue.Normalize).Distinct().ToArray();

        private DbCommand CreateCommand(DbConnection connection, object[] keys)
        {
            var command = connection.CreateCommand();
            try
            {
                var names = new List<string>();
                foreach (var key in keys)
                {
                    var name = dialect.GetParameterPlaceholder(names.Count);
                    names.Add(name);
                    command.Parameters.Add(dialect.CreateParameter(new QueryParameter(name, key)));
                }
                command.CommandText = $"{selectSql} WHERE {dialect.QuoteIdentifier(relation.ForeignKeyColumn)} IN ({string.Join(", ", names)})";
                return command;
            }
            catch { command.Dispose(); throw; }
        }

        public void Load(DbConnection connection, object[] parents)
        {
            var children = new List<TChild>();
            foreach (var keys in Keys(parents).Chunk(2000))
            {
                using var command = CreateCommand(connection, keys);
                using var reader = command.ExecuteReader();
                children.AddRange(mapping.Read(reader));
            }
            Assign(parents, children);
        }

        public async Task LoadAsync(DbConnection connection, object[] parents, CancellationToken token)
        {
            var children = new List<TChild>();
            foreach (var keys in Keys(parents).Chunk(2000))
            {
                token.ThrowIfCancellationRequested();
                await using var command = CreateCommand(connection, keys);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                children.AddRange(await mapping.ReadAsync(reader, token).ConfigureAwait(false));
            }
            token.ThrowIfCancellationRequested();
            Assign(parents, children);
        }

        private void Assign(object[] parents, List<TChild> children)
        {
            var groups = children.Where(child => relation.ForeignKey.GetValue(child) is not null)
                .ToLookup(child => SqlWriteValue.Normalize(relation.ForeignKey.GetValue(child)!));
            foreach (var parent in parents)
            {
                var key = relation.ParentKey.GetValue(parent);
                var matches = key is null ? [] : groups[SqlWriteValue.Normalize(key)].ToList();
                relation.Property.SetValue(parent, relation.Property.PropertyType.IsArray ? matches.ToArray() : matches);
            }
        }
    }
}
