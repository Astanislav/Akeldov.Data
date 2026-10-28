using System.Linq.Expressions;
using Microsoft.Data.SqlClient;

namespace Akeldov.Data.SqlServer;

public static class SqlConnectionExtensions
{
    /// <summary>Inserts a row and its Reference entities using properties marked with Column.</summary>
    /// <remarks>References are inserted first and their Key values are copied to local ForeignKey properties.
    /// Graphs are inserted in a transaction. Keys must be supplied by the caller; generated columns are not inferred or returned.
    /// Connections opened here are closed afterwards.</remarks>
    public static void Create<T>(this SqlConnection connection, T entity) where T : class, new()
        => CreateGraphExecutor.Create(connection, SqlServerDialect.Instance, entity);

    /// <summary>Asynchronously inserts a row and its Reference entities, dependencies first.</summary>
    /// <remarks>Multi-row graphs use one transaction. On failure or cancellation, rollback is attempted and
    /// changed foreign keys in memory are restored. Keys must be supplied by the caller.
    /// Cancellation is passed to the driver. Connections opened here are closed afterwards.</remarks>
    public static Task CreateAsync<T>(this SqlConnection connection, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
        => CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance, entity, cancellationToken);

    /// <summary>Reads all rows from the mapped table.</summary>
    /// <remarks>A connection opened by this method is closed after reading.</remarks>
    public static List<T> Select<T>(this SqlConnection connection) where T : class, new()
        => SelectExecutor.Select<T>(connection, SqlServerDialect.Instance);

    /// <summary>Reads matching rows using a parameterized SQL predicate.</summary>
    /// <remarks>Unsupported expressions throw before the connection is opened.</remarks>
    public static List<T> Select<T>(this SqlConnection connection, Expression<Func<T, bool>> predicate)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(predicate);
        return SelectExecutor.Select(connection, SqlServerDialect.Instance, predicate);
    }
}
