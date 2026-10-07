using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;

namespace Akeldov.Data;

internal sealed class TableMapping<T> where T : class, new()
{
    private readonly (PropertyInfo Property, string Name)[] columns;

    private TableMapping(TableAttribute table, (PropertyInfo Property, string Name)[] columns)
    {
        Table = table;
        this.columns = columns;
    }

    internal TableAttribute Table { get; }

    internal IEnumerable<string> ColumnNames => columns.Select(column => column.Name);

    internal IEnumerable<(PropertyInfo Property, string Name)> Columns => columns;

    internal string CreateSelectSql(ISqlDialect dialect)
    {
        var names = new HashSet<string>(dialect.ColumnNameComparer);
        foreach (var column in columns)
        {
            if (!names.Add(column.Name))
            {
                throw new InvalidOperationException($"Column '{column.Name}' is mapped more than once on '{typeof(T).FullName}'.");
            }
        }

        var projection = string.Join(", ", columns.Select(column => dialect.QuoteIdentifier(column.Name)));
        return $"SELECT {projection} FROM {dialect.QuoteIdentifier(Table.Schema)}.{dialect.QuoteIdentifier(Table.Name)}";
    }

    internal string GetColumnName(PropertyInfo property)
    {
        foreach (var column in columns)
        {
            if (column.Property.Name == property.Name && column.Property.DeclaringType == property.DeclaringType)
            {
                return column.Name;
            }
        }

        throw new NotSupportedException($"Property '{property.Name}' must be mapped with a Column attribute to be used in a predicate.");
    }

    internal static TableMapping<T> Create()
    {
        var type = typeof(T);
        var table = type.GetCustomAttribute<TableAttribute>()
            ?? throw new InvalidOperationException($"Type '{type.FullName}' must have a Table attribute.");

        var columns = new List<(PropertyInfo Property, string Name)>();
        var names = new HashSet<string>(StringComparer.Ordinal);

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

        return new TableMapping<T>(table, columns.ToArray());
    }

    internal List<T> Read(IDataReader reader, CancellationToken cancellationToken = default)
    {
        var ordinals = columns.Select(column => reader.GetOrdinal(column.Name)).ToArray();
        var rows = new List<T>();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(ReadRow(reader, ordinals));
        }

        return rows;
    }

    internal async Task<List<T>> ReadAsync(DbDataReader reader, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordinals = columns.Select(column => reader.GetOrdinal(column.Name)).ToArray();
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(ReadRow(reader, ordinals));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return rows;
    }

    private T ReadRow(IDataReader reader, int[] ordinals)
    {
        var row = new T();
        for (var index = 0; index < columns.Length; index++)
        {
            var (property, name) = columns[index];
            var value = reader.GetValue(ordinals[index]);
            property.SetValue(row, ConvertValue(value, property.PropertyType, name));
        }

        return row;
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
            if (targetType == typeof(DateTimeOffset) && value is DateTime dateTime)
            {
                var utc = dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime.ToUniversalTime();
                return new DateTimeOffset(utc);
            }

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

}
