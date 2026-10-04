using System.Data.Common;
using System.Globalization;
using System.Reflection;

namespace Akeldov.Data;

internal sealed class TableMapping<T> where T : class, new()
{
    private readonly (PropertyInfo Property, string Name)[] columns;

    private TableMapping(string commandText, (PropertyInfo Property, string Name)[] columns)
    {
        CommandText = commandText;
        this.columns = columns;
    }

    internal string CommandText { get; }

    internal static TableMapping<T> Create()
    {
        var type = typeof(T);
        var table = type.GetCustomAttribute<TableAttribute>()
            ?? throw new InvalidOperationException($"Type '{type.FullName}' must have a Table attribute.");

        var columns = new List<(PropertyInfo Property, string Name)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var column = property.GetCustomAttribute<ColumnAttribute>();
            if (column is null)
            {
                continue;
            }

            if (property.GetSetMethod() is null || property.GetIndexParameters().Length != 0)
            {
                throw new InvalidOperationException(
                    $"Mapped property '{type.FullName}.{property.Name}' must have a public setter and must not be an indexer.");
            }

            if (!names.Add(column.Name))
            {
                throw new InvalidOperationException($"Column '{column.Name}' is mapped more than once on '{type.FullName}'.");
            }

            columns.Add((property, column.Name));
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"Type '{type.FullName}' must have at least one property with a Column attribute.");
        }

        var projection = string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)));
        var commandText = $"SELECT {projection} FROM {QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)}";

        return new TableMapping<T>(commandText, columns.ToArray());
    }

    internal List<T> Read(DbDataReader reader)
    {
        var ordinals = columns.Select(column => reader.GetOrdinal(column.Name)).ToArray();
        var rows = new List<T>();

        while (reader.Read())
        {
            var row = new T();
            for (var index = 0; index < columns.Length; index++)
            {
                var (property, name) = columns[index];
                var value = reader.GetValue(ordinals[index]);
                property.SetValue(row, ConvertValue(value, property.PropertyType, name));
            }

            rows.Add(row);
        }

        return rows;
    }

    private static object? ConvertValue(object value, Type propertyType, string columnName)
    {
        var nullableType = Nullable.GetUnderlyingType(propertyType);
        var targetType = nullableType ?? propertyType;

        if (value is DBNull)
        {
            if (propertyType.IsValueType && nullableType is null)
            {
                throw new InvalidOperationException($"Column '{columnName}' is NULL but property type '{propertyType}' is not nullable.");
            }

            return null;
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        try
        {
            if (targetType.IsEnum)
            {
                var enumValue = Convert.ChangeType(value, Enum.GetUnderlyingType(targetType), CultureInfo.InvariantCulture);
                return Enum.ToObject(targetType, enumValue);
            }

            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException($"Cannot map column '{columnName}' to property type '{propertyType}'.", exception);
        }
    }

    private static string QuoteIdentifier(string name) => $"[{name.Replace("]", "]]")}]";
}
