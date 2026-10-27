using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using ClickHouse.Driver.ADO;

namespace Akeldov.Data.ClickHouse;

/// <summary>Reads and inserts mapped models using the official ClickHouse ADO.NET driver.</summary>
/// <remarks>Table.Schema specifies the database. Connections opened by an operation are closed afterwards.</remarks>
public static class ClickHouseConnectionExtensions
{
    /// <summary>Inserts a row and its Reference entities, dependencies first.</summary>
    /// <remarks>Caller-supplied Key values are copied to local ForeignKey properties. Values are parameterized.
    /// Commands are not transactional: earlier inserts remain on failure, but changed foreign keys in memory are restored.
    /// Generated columns are not inferred or returned. Connections opened here are closed afterwards.</remarks>
    public static void Create<T>(this ClickHouseConnection connection, T entity) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entity);
        var builder = new CommandBuilder(connection);
        using var graph = new CreateGraph(ClickHouseDialect.Instance.ColumnNameComparer, builder);
        var shouldClose = connection.State == ConnectionState.Closed;
        try
        {
            graph.Prepare(entity);
            if (shouldClose) connection.Open();
            foreach (var command in builder.Commands) command.ExecuteNonQuery();
            graph.Complete();
        }
        finally
        {
            foreach (var command in builder.Commands) command.Dispose();
            if (shouldClose && connection.State != ConnectionState.Closed) connection.Close();
        }
    }

    /// <summary>Asynchronously inserts a row and its Reference entities, dependencies first.</summary>
    /// <remarks>Cancellation is passed to the driver and checked before each command.
    /// Commands are not transactional: earlier inserts remain on failure or cancellation, but changed foreign keys in memory are restored.</remarks>
    public static async Task CreateAsync<T>(this ClickHouseConnection connection, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new CommandBuilder(connection);
        using var graph = new CreateGraph(ClickHouseDialect.Instance.ColumnNameComparer, builder);
        var shouldClose = connection.State == ConnectionState.Closed;
        try
        {
            graph.Prepare(entity);
            if (shouldClose) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            foreach (var command in builder.Commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            graph.Complete();
        }
        finally
        {
            foreach (var command in builder.Commands) await command.DisposeAsync().ConfigureAwait(false);
            if (shouldClose && connection.State != ConnectionState.Closed)
                await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private sealed class CommandBuilder(ClickHouseConnection connection) : ICreateGraphCommandBuilder
    {
        public List<DbCommand> Commands { get; } = [];

        public void Add<T>(T entity) where T : class, new()
            => Commands.Add(CreateExecutor.CreateCommand(connection, ClickHouseDialect.Instance, entity));
    }

    /// <summary>Reads all rows, populating properties marked with Column.</summary>
    public static List<T> Select<T>(this ClickHouseConnection connection) where T : class, new()
        => SelectExecutor.Select<T>(connection, ClickHouseDialect.Instance);

    /// <summary>Reads matching rows using a parameterized SQL predicate.</summary>
    /// <remarks>Unsupported expressions or parameter types throw before the connection is opened.</remarks>
    public static List<T> Select<T>(this ClickHouseConnection connection, Expression<Func<T, bool>> predicate)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(predicate);
        return SelectExecutor.Select(connection, ClickHouseDialect.Instance, predicate);
    }

    /// <summary>Asynchronously reads all mapped rows.</summary>
    public static Task<List<T>> SelectAsync<T>(this ClickHouseConnection connection,
        CancellationToken cancellationToken = default) where T : class, new()
        => SelectExecutor.SelectAsync<T>(connection, ClickHouseDialect.Instance, cancellationToken: cancellationToken);

    /// <summary>Asynchronously reads matching rows, passing cancellation to the driver and result reader.</summary>
    public static Task<List<T>> SelectAsync<T>(this ClickHouseConnection connection, Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(predicate);
        return SelectExecutor.SelectAsync(connection, ClickHouseDialect.Instance, predicate, cancellationToken);
    }
}
