using System.Linq.Expressions;
using Microsoft.Data.SqlClient;

namespace Akeldov.Data.SqlServer;

public static class SqlConnectionExtensions
{
    /// <summary>Inserts one row using all properties marked with Column.</summary>
    /// <remarks>Values are parameterized. Generated columns are not inferred or returned. Connections opened here are closed afterwards.</remarks>
    public static void Create<T>(this SqlConnection connection, T entity) where T : class, new()
        => CreateExecutor.Create(connection, SqlServerDialect.Instance, entity);

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
