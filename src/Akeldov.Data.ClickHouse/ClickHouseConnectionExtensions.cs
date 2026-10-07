using System.Linq.Expressions;
using ClickHouse.Driver.ADO;

namespace Akeldov.Data.ClickHouse;

/// <summary>Reads mapped models from ClickHouse using the official ADO.NET driver.</summary>
/// <remarks>Table.Schema specifies the database. Connections opened by a select are closed afterwards.</remarks>
public static class ClickHouseConnectionExtensions
{
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
