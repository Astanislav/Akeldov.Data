using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace Akeldov.Data.PostgreSql;

internal sealed class PostgreSqlDialect : ISqlDialect
{
    internal static PostgreSqlDialect Instance { get; } = new();

    public StringComparer ColumnNameComparer => StringComparer.Ordinal;

    public string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
    public string GetParameterPlaceholder(int index) => $"${index + 1}";
    public string FormatBooleanCondition(string operand) => $"({operand} IS TRUE)";

    public DbParameter CreateParameter(QueryParameter parameter)
    {
        var value = parameter.Value is byte number ? (object)(short)number : parameter.Value;
        var type = value switch
        {
            bool => NpgsqlDbType.Boolean,
            short => NpgsqlDbType.Smallint,
            int => NpgsqlDbType.Integer,
            long => NpgsqlDbType.Bigint,
            float => NpgsqlDbType.Real,
            double => NpgsqlDbType.Double,
            decimal => NpgsqlDbType.Numeric,
            string => NpgsqlDbType.Text,
            byte[] => NpgsqlDbType.Bytea,
            Guid => NpgsqlDbType.Uuid,
            DateTime { Kind: DateTimeKind.Utc } => NpgsqlDbType.TimestampTz,
            DateTime => NpgsqlDbType.Timestamp,
            DateTimeOffset => NpgsqlDbType.TimestampTz,
            TimeSpan => NpgsqlDbType.Interval,
            _ => throw new NotSupportedException($"Predicate parameter type '{value.GetType()}' is not supported by PostgreSQL.")
        };
        return new NpgsqlParameter { NpgsqlDbType = type, Value = value };
    }
}
