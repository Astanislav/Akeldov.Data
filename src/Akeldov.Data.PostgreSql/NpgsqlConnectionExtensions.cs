using System.Linq.Expressions;
using Npgsql;

namespace Akeldov.Data.PostgreSql;

public static class NpgsqlConnectionExtensions
{
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
