using System.Data;
using System.Data.Common;

namespace Akeldov.Data;

internal static class CreateExecutor
{
    internal static void Create<T>(DbConnection connection, ISqlDialect dialect, T entity) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entity);
        using var command = CreateCommand(connection, dialect, entity);
        var shouldClose = connection.State == ConnectionState.Closed;
        try
        {
            if (shouldClose) connection.Open();
            command.ExecuteNonQuery();
        }
        finally
        {
            if (shouldClose) connection.Close();
        }
    }

    internal static async Task CreateAsync<T>(DbConnection connection, ISqlDialect dialect, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = CreateCommand(connection, dialect, entity);
        var shouldClose = connection.State == ConnectionState.Closed;
        try
        {
            if (shouldClose) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    internal static DbCommand CreateCommand<T>(DbConnection connection, ISqlDialect dialect, T entity)
        where T : class, new()
    {
        var mapping = TableMapping<T>.Create();
        mapping.ValidateColumnNames(dialect.ColumnNameComparer);
        mapping.ValidateGetters();
        var columns = mapping.Columns.ToArray();
        var command = connection.CreateCommand();
        try
        {
            var values = new string[columns.Length];
            for (var index = 0; index < columns.Length; index++)
            {
                var value = columns[index].Property.GetValue(entity);
                // A literal NULL needs no parameter type inference, including in ClickHouse.
                if (value is null)
                {
                    values[index] = "NULL";
                    continue;
                }

                var parameterIndex = command.Parameters.Count;
                values[index] = dialect.GetParameterPlaceholder(parameterIndex);
                command.Parameters.Add(dialect.CreateParameter(new QueryParameter($"@p{parameterIndex}", SqlWriteValue.Normalize(value))));
            }

            var names = string.Join(", ", columns.Select(column => dialect.QuoteIdentifier(column.Name)));
            command.CommandText = $"INSERT INTO {dialect.QuoteIdentifier(mapping.Table.Schema)}.{dialect.QuoteIdentifier(mapping.Table.Name)} ({names}) VALUES ({string.Join(", ", values)})";
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }
}
