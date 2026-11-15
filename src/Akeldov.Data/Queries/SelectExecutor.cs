using System.Data;
using System.Data.Common;
using System.Linq.Expressions;

namespace Akeldov.Data;

internal static class SelectExecutor
{
    internal static List<T> Select<T>(
        DbConnection connection, ISqlDialect dialect, Expression<Func<T, bool>>? predicate = null)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        var mapping = TableMapping<T>.Create();
        var loaders = CollectionLoader.Prepare(mapping, dialect);
        using var command = CreateSelectCommand(connection, mapping, dialect, predicate);
        var shouldClose = connection.State == ConnectionState.Closed;

        try
        {
            if (shouldClose)
            {
                connection.Open();
            }

            List<T> rows;
            using (var reader = command.ExecuteReader()) rows = mapping.Read(reader);
            var parents = rows.Cast<object>().ToArray();
            foreach (var loader in loaders) loader.Load(connection, parents);
            return rows;
        }
        finally
        {
            if (shouldClose)
            {
                connection.Close();
            }
        }
    }

    internal static async Task<List<T>> SelectAsync<T>(
        DbConnection connection, ISqlDialect dialect, Expression<Func<T, bool>>? predicate = null,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        var mapping = TableMapping<T>.Create();
        var loaders = CollectionLoader.Prepare(mapping, dialect);
        await using var command = CreateSelectCommand(connection, mapping, dialect, predicate);
        var shouldClose = connection.State == ConnectionState.Closed;

        try
        {
            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            List<T> rows;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                rows = await mapping.ReadAsync(reader, cancellationToken).ConfigureAwait(false);
            var parents = rows.Cast<object>().ToArray();
            foreach (var loader in loaders)
                await loader.LoadAsync(connection, parents, cancellationToken).ConfigureAwait(false);
            return rows;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    internal static DbCommand CreateSelectCommand<T>(
        DbConnection connection, TableMapping<T> mapping, ISqlDialect dialect,
        Expression<Func<T, bool>>? predicate = null)
        where T : class, new()
    {
        var sql = mapping.CreateSelectSql(dialect);
        var parameters = Array.Empty<QueryParameter>();
        if (predicate is not null)
        {
            var translation = SqlPredicateTranslator<T>.Translate(mapping, predicate, dialect);
            sql += $" WHERE {translation.Sql}";
            parameters = translation.Parameters;
        }

        var command = connection.CreateCommand();
        try
        {
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(dialect.CreateParameter(parameter));
            }

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }
}
