using System.Data;
using System.Data.Common;
using ClickHouse.Driver.ADO;

namespace Akeldov.Data.ClickHouse;

internal static class ClickHouseUpdateExecutor
{
    internal static void Update<T>(ClickHouseConnection connection, T entity) where T : class, new()
    {
        using var command = CreateCommand(connection, entity);
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

    internal static async Task UpdateAsync<T>(ClickHouseConnection connection, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = CreateCommand(connection, entity);
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

    internal static DbCommand CreateCommand<T>(ClickHouseConnection connection, T entity) where T : class, new()
        => UpdateExecutor.CreateCommand(connection, ClickHouseDialect.Instance, entity,
            static (table, assignments, predicate) =>
                $"ALTER TABLE {table} UPDATE {assignments} WHERE {predicate} SETTINGS mutations_sync = 1");
}
