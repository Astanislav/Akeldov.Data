using System.Data.Common;

namespace Akeldov.Data;

internal interface ISqlDialect
{
    StringComparer ColumnNameComparer { get; }
    string QuoteIdentifier(string name);
    string GetParameterPlaceholder(int index);
    string FormatBooleanCondition(string operand);
    DbParameter CreateParameter(QueryParameter parameter);
}
