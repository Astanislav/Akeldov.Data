using System.Linq.Expressions;
using Kusto.Data.Common;

namespace Akeldov.Data.Kusto;

/// <summary>Reads mapped models with Kusto Query Language.</summary>
/// <remarks>TableAttribute.Schema specifies the Kusto database. The caller owns the query provider.</remarks>
public static class KustoQueryProviderExtensions
{
    /// <summary>Reads all rows from the mapped table.</summary>
    public static List<T> Select<T>(this ICslQueryProvider provider) where T : class, new()
        => Execute<T>(provider);

    /// <summary>Reads matching rows using a parameterized KQL predicate.</summary>
    /// <remarks>Unsupported expressions throw before a query is sent.</remarks>
    public static List<T> Select<T>(this ICslQueryProvider provider, Expression<Func<T, bool>> predicate)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Execute(provider, predicate);
    }

    /// <summary>Asynchronously reads all rows from the mapped table.</summary>
    public static Task<List<T>> SelectAsync<T>(this ICslQueryProvider provider, CancellationToken cancellationToken = default)
        where T : class, new()
        => ExecuteAsync<T>(provider, null, cancellationToken);

    /// <summary>Asynchronously reads matching rows using a parameterized KQL predicate.</summary>
    public static Task<List<T>> SelectAsync<T>(this ICslQueryProvider provider, Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return ExecuteAsync(provider, predicate, cancellationToken);
    }

    private static List<T> Execute<T>(ICslQueryProvider provider, Expression<Func<T, bool>>? predicate = null)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(provider);
        var mapping = TableMapping<T>.Create();
        var query = KqlSelectQuery.Create(mapping, predicate);
        using var reader = provider.ExecuteQuery(query.Database, query.Text, query.Properties);
        return mapping.Read(reader);
    }

    private static async Task<List<T>> ExecuteAsync<T>(ICslQueryProvider provider, Expression<Func<T, bool>>? predicate,
        CancellationToken cancellationToken) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(provider);
        cancellationToken.ThrowIfCancellationRequested();
        var mapping = TableMapping<T>.Create();
        var query = KqlSelectQuery.Create(mapping, predicate);
        using var reader = await provider.ExecuteQueryAsync(query.Database, query.Text, query.Properties, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return mapping.Read(reader, cancellationToken);
    }
}
