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

    private sealed class CommandBuilder(DbConnection connection, ISqlDialect dialect, List<DbCommand> commands)
        : ICreateGraphCommandBuilder
    {
        public void Add<T>(T entity) where T : class, new()
            => commands.Add(CreateExecutor.CreateCommand(connection, dialect, entity));
    }
}
