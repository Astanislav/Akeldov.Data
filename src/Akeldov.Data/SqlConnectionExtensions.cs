using System.Data;
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
    {
        ArgumentNullException.ThrowIfNull(connection);

        var mapping = TableMapping<T>.Create();
        var shouldClose = connection.State == ConnectionState.Closed;

        try
        {
            if (shouldClose)
            {
                connection.Open();
            }

            using var command = connection.CreateCommand();
            command.CommandText = mapping.CommandText;

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
