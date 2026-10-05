using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Akeldov.Data.SqlServer;

internal sealed class SqlServerDialect : ISqlDialect
{
    internal static SqlServerDialect Instance { get; } = new();

    public StringComparer ColumnNameComparer => StringComparer.OrdinalIgnoreCase;

    public string QuoteIdentifier(string name) => $"[{name.Replace("]", "]]")}]";
    public string GetParameterPlaceholder(int index) => $"@p{index}";
    public string FormatBooleanCondition(string operand) => $"({operand} = 1)";

    public DbParameter CreateParameter(QueryParameter parameter)
    {
        var result = new SqlParameter(parameter.ParameterName, parameter.Value);
        if (parameter.Value is DateTime)
        {
            result.SqlDbType = SqlDbType.DateTime2;
        }

        return result;
    }
}
