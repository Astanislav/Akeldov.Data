using System.Data.Common;
using ClickHouse.Driver.ADO.Parameters;

namespace Akeldov.Data.ClickHouse;

internal sealed class ClickHouseDialect : ISqlDialect
{
    internal static ClickHouseDialect Instance { get; } = new();

    public StringComparer ColumnNameComparer => StringComparer.Ordinal;
    public string QuoteIdentifier(string name) => $"`{name.Replace("\\", "\\\\").Replace("`", "\\`")}`";
    public string GetParameterPlaceholder(int index) => $"@p{index}";
    public string FormatBooleanCondition(string operand) => $"({operand} = 1)";

    public DbParameter CreateParameter(QueryParameter parameter)
    {
        var value = parameter.Value;
        if (value is DateTime date)
        {
            value = date.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
                : date.ToUniversalTime();
        }
        else if (value is DateTimeOffset offset)
        {
            value = offset.UtcDateTime;
        }

        var type = value switch
        {
            bool => "Bool",
            byte => "UInt8",
            short => "Int16",
            int => "Int32",
            long => "Int64",
            float number when float.IsFinite(number) => "Float32",
            double number when double.IsFinite(number) => "Float64",
            decimal number => $"Decimal(38, {(decimal.GetBits(number)[3] >> 16) & 0xff})",
            string => "String",
            Guid => "UUID",
            DateTime => "DateTime64(7, 'UTC')",
            _ => throw new NotSupportedException($"Predicate parameter type or value '{value.GetType()}' is not supported by ClickHouse.")
        };

        return new ClickHouseDbParameter
        {
            ParameterName = parameter.ParameterName.TrimStart('@'),
            ClickHouseType = type,
            Value = value
        };
    }
}
