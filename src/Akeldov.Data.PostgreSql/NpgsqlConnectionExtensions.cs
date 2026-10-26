using System.Linq.Expressions;
using Npgsql;

namespace Akeldov.Data.PostgreSql;

public static class NpgsqlConnectionExtensions
{
    /// <summary>Inserts a row and its Reference entities using properties marked with Column.</summary>
    /// <remarks>References are inserted first and their Key values are copied to local ForeignKey properties.
    /// Graphs are inserted in a transaction. Keys must be supplied by the caller; generated columns are not inferred or returned.
    /// Connections opened here are closed afterwards.</remarks>
    public static void Create<T>(this NpgsqlConnection connection, T entity) where T : class, new()
        => CreateGraphExecutor.Create(connection, PostgreSqlDialect.Instance, entity);

    /// <summary>
    /// Reads all rows from the table mapped to T, populating properties marked with Column.
    /// </summary>
    /// <remarks>A connection opened by this method is closed after reading.</remarks>
    public static List<T> Select<T>(this NpgsqlConnection connection) where T : class, new()
        => SelectExecutor.Select<T>(connection, PostgreSqlDialect.Instance);

    /// <summary>
    /// Reads matching rows using a parameterized SQL predicate.
    /// </summary>
    /// <remarks>Unsupported expressions throw before the connection is opened.</remarks>
    public static List<T> Select<T>(this NpgsqlConnection connection, Expression<Func<T, bool>> predicate)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(predicate);
        return SelectExecutor.Select(connection, PostgreSqlDialect.Instance, predicate);
    }
}
