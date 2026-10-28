using System.Data;
using System.Data.Common;
using System.Reflection;

namespace Akeldov.Data;

/// <summary>Updates mapped columns by caller-supplied keys. Provider extensions can delegate to this executor.</summary>
/// <remarks>Updates one model's table; Reference entities are not traversed. Keys are never assigned in SET.</remarks>
internal static class UpdateExecutor
{
    internal static void Update<T>(DbConnection connection, ISqlDialect dialect, T entity) where T : class, new()
    {
        using var command = CreateCommand(connection, dialect, entity);
        var shouldClose = connection.State == ConnectionState.Closed;
        try
        {
            if (shouldClose) connection.Open();
            command.ExecuteNonQuery();
        }
        finally
        {
            if (shouldClose && connection.State != ConnectionState.Closed) connection.Close();
        }
    }

    internal static async Task UpdateAsync<T>(DbConnection connection, ISqlDialect dialect, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = CreateCommand(connection, dialect, entity);
        var shouldClose = connection.State == ConnectionState.Closed;
        try
        {
            if (shouldClose) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (shouldClose && connection.State != ConnectionState.Closed)
                await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    internal static DbCommand CreateCommand<T>(DbConnection connection, ISqlDialect dialect, T entity)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(entity);
        var mapping = TableMapping<T>.Create();
        mapping.ValidateColumnNames(dialect.ColumnNameComparer);
        mapping.ValidateGetters();
        var columns = mapping.Columns.ToArray();
        var keyProperties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.IsDefined(typeof(KeyAttribute))).ToArray();
        if (keyProperties.Length == 0)
            throw new InvalidOperationException($"Type '{typeof(T).FullName}' must have at least one Key property to update a row.");
        foreach (var property in keyProperties)
        {
            if (!property.IsDefined(typeof(ColumnAttribute)))
                throw new InvalidOperationException($"Key property '{typeof(T).FullName}.{property.Name}' must have a Column attribute.");
        }

        var keys = columns.Where(column => column.Property.IsDefined(typeof(KeyAttribute))).ToArray();
        var assignments = columns.Where(column => !column.Property.IsDefined(typeof(KeyAttribute))).ToArray();
        if (assignments.Length == 0)
            throw new InvalidOperationException($"Type '{typeof(T).FullName}' must have at least one non-key Column to update.");
        var keyValues = keys.Select(key => key.Property.GetValue(entity)
            ?? throw new InvalidOperationException($"Key property '{typeof(T).FullName}.{key.Property.Name}' must not be null when updating a row.")).ToArray();

        var command = connection.CreateCommand();
        try
        {
            var set = assignments.Select(column =>
                $"{dialect.QuoteIdentifier(column.Name)} = {AddValue(command, dialect, column.Property.GetValue(entity))}").ToArray();
            var where = keys.Select((key, index) =>
                $"{dialect.QuoteIdentifier(key.Name)} = {AddValue(command, dialect, keyValues[index])}").ToArray();
            command.CommandText = $"UPDATE {dialect.QuoteIdentifier(mapping.Table.Schema)}.{dialect.QuoteIdentifier(mapping.Table.Name)} SET {string.Join(", ", set)} WHERE {string.Join(" AND ", where)}";
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private static string AddValue(DbCommand command, ISqlDialect dialect, object? value)
    {
        if (value is null) return "NULL";
        var index = command.Parameters.Count;
        var placeholder = dialect.GetParameterPlaceholder(index);
        command.Parameters.Add(dialect.CreateParameter(new QueryParameter($"@p{index}", SqlWriteValue.Normalize(value))));
        return placeholder;
    }
}
