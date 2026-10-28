using System.Globalization;

namespace Akeldov.Data;

internal static class SqlWriteValue
{
    internal static object Normalize(object value)
    {
        if (value.GetType().IsEnum)
            value = Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture);

        return value switch
        {
            char character => character.ToString(),
            sbyte number => (short)number,
            ushort number => (int)number,
            uint number => (long)number,
            ulong number => (decimal)number,
            string or bool or byte or short or int or long or float or double or decimal
                or Guid or DateTime or DateTimeOffset or TimeSpan or byte[] => value,
            _ => throw new NotSupportedException($"Write value type '{value.GetType()}' is not supported.")
        };
    }
}
