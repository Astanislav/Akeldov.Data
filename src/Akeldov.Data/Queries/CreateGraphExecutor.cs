using System.Data;
using System.Data.Common;

namespace Akeldov.Data;

internal static class CreateGraphExecutor
{
    internal static void Create<T>(DbConnection connection, ISqlDialect dialect, T entity) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entity);

        var commands = new List<DbCommand>();
        var graph = new CreateGraph(dialect.ColumnNameComparer, new CommandBuilder(connection, dialect, commands));
        var shouldClose = connection.State == ConnectionState.Closed;
        try
        {
            graph.Prepare(entity);
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
                graph.Complete();
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
            graph.Dispose();
            if (shouldClose && connection.State != ConnectionState.Closed) connection.Close();
        }
    }

    internal static async Task CreateAsync<T>(DbConnection connection, ISqlDialect dialect, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();

        var commands = new List<DbCommand>();
        using var graph = new CreateGraph(dialect.ColumnNameComparer, new CommandBuilder(connection, dialect, commands));
        var shouldClose = connection.State == ConnectionState.Closed;
        try
        {
            graph.Prepare(entity);
            if (shouldClose) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = commands.Count > 1
                ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
            try
            {
                foreach (var command in commands)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    command.Transaction = transaction;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                if (transaction is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                graph.Complete();
            }
            catch
            {
                // Roll back even when the operation's token is canceled, preserving the original exception.
                if (transaction is not null)
                {
                    try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
                throw;
            }
        }
        finally
        {
            try
            {
                foreach (var command in commands) await command.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (shouldClose && connection.State != ConnectionState.Closed)
                    await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class CommandBuilder(DbConnection connection, ISqlDialect dialect, List<DbCommand> commands)
        : ICreateGraphCommandBuilder
    {
        public void Add<T>(T entity) where T : class, new()
            => commands.Add(CreateExecutor.CreateCommand(connection, dialect, entity));
    }
}
