using System.Globalization;

namespace Akeldov.Data.Kusto;

internal sealed record KqlParameter(string Name, string Type, string Value)
{
    internal static string GetScalarType(System.Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short)
            || type == typeof(ushort) || type == typeof(int)) return "int";
        if (type == typeof(uint) || type == typeof(long) || type == typeof(ulong)) return "long";
        if (type == typeof(float) || type == typeof(double)) return "real";
        if (type == typeof(Guid)) return "guid";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "datetime";
        if (type == typeof(TimeSpan)) return "timespan";
        throw new NotSupportedException($"Predicate value type '{type}' is not supported by KQL.");
    }

    internal static KqlParameter Create(int index, object value)
    {
        var type = value.GetType();
        var scalarType = GetScalarType(type);
        if (type.IsEnum)
        {
            value = Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
        }

        // Kusto long is signed; reject values that cannot be represented exactly.
        if (value is ulong number && number > long.MaxValue)
        {
            throw new NotSupportedException("KQL cannot represent UInt64 values greater than Int64.MaxValue.");
        }

        var serialized = value switch
        {
            string text => text,
            bool boolean => boolean ? "true" : "false",
            DateTime dateTime => $"datetime({ToUtc(dateTime).ToString("O", CultureInfo.InvariantCulture)})",
            DateTimeOffset dateTime => $"datetime({dateTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)})",
            TimeSpan duration => $"timespan({duration.ToString("c", CultureInfo.InvariantCulture)})",
            Guid guid => $"guid({guid:D})",
            float real when float.IsFinite(real) => real.ToString("R", CultureInfo.InvariantCulture),
            double real when double.IsFinite(real) => real.ToString("R", CultureInfo.InvariantCulture),
            float or double => throw new NotSupportedException("Non-finite floating-point parameters are not supported by KQL."),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
        };
        return new KqlParameter($"akeldov_p{index}", scalarType, serialized);
    }

    private static DateTime ToUtc(DateTime dateTime)
        => dateTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : dateTime.ToUniversalTime();
}
