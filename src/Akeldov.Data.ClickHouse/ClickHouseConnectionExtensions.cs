using System.Linq.Expressions;
using ClickHouse.Driver.ADO;

namespace Akeldov.Data.ClickHouse;

/// <summary>Reads and inserts mapped models using the official ClickHouse ADO.NET driver.</summary>
/// <remarks>Table.Schema specifies the database. Connections opened by an operation are closed afterwards.</remarks>
public static class ClickHouseConnectionExtensions
{
    /// <summary>Inserts one row using all properties marked with Column.</summary>
    /// <remarks>Values are parameterized. Generated columns are not inferred or returned. Connections opened here are closed afterwards.</remarks>
    public static void Create<T>(this ClickHouseConnection connection, T entity) where T : class, new()
        => CreateExecutor.Create(connection, ClickHouseDialect.Instance, entity);

    /// <summary>Asynchronously inserts one mapped row.</summary>
    public static Task CreateAsync<T>(this ClickHouseConnection connection, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
        => CreateExecutor.CreateAsync(connection, ClickHouseDialect.Instance, entity, cancellationToken);

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
