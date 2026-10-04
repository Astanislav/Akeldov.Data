using System.Data;
using System.Linq.Expressions;
using Microsoft.Data.SqlClient;

namespace Akeldov.Data;

public static class SqlConnectionExtensions
{
    /// <summary>
    /// Reads all rows from the table mapped to <typeparamref name="T"/>.
    /// Only properties marked with <see cref="ColumnAttribute"/> are populated.
    /// </summary>
    /// <remarks>
    /// A connection opened by this method is closed after reading.
    /// An already open connection remains open.
    /// </remarks>
    public static List<T> Select<T>(this SqlConnection connection) where T : class, new()
        => SelectCore<T>(connection, null);

    /// <summary>
    /// Reads rows matching the predicate from the table mapped to <typeparamref name="T"/>.
    /// Predicate values are passed as SQL parameters.
    /// </summary>
    /// <remarks>
    /// Supports comparisons, logical AND, OR, NOT, and null checks on mapped properties.
    /// Unsupported expressions throw <see cref="NotSupportedException"/> before opening the connection.
    /// A connection opened by this method is closed after reading.
    /// </remarks>
    public static List<T> Select<T>(this SqlConnection connection, Expression<Func<T, bool>> predicate)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(predicate);

        return SelectCore(connection, predicate);
    }

    internal static SqlCommand CreateSelectCommand<T>(
        SqlConnection connection, TableMapping<T> mapping, Expression<Func<T, bool>>? predicate = null)
        where T : class, new()
    {
        var translation = predicate is null
            ? (Sql: (string?)null, Parameters: Array.Empty<SqlParameter>())
            : SqlPredicateTranslator<T>.Translate(mapping, predicate);

        var command = connection.CreateCommand();
        command.CommandText = translation.Sql is null
            ? mapping.CommandText
            : $"{mapping.CommandText} WHERE {translation.Sql}";
        command.Parameters.AddRange(translation.Parameters);
        return command;
    }

    private static List<T> SelectCore<T>(SqlConnection connection, Expression<Func<T, bool>>? predicate)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(connection);

        var mapping = TableMapping<T>.Create();
        using var command = CreateSelectCommand(connection, mapping, predicate);
        var shouldClose = connection.State == ConnectionState.Closed;

        try
        {
            if (shouldClose)
            {
                connection.Open();
            }

            using var reader = command.ExecuteReader();
            return mapping.Read(reader);
        }
        finally
        {
            if (shouldClose)
            {
                connection.Close();
            }
        }
    }
}
