using System.Data.Common;

namespace Akeldov.Data;

internal interface ISqlDialect
{
    StringComparer ColumnNameComparer { get; }
    string QuoteIdentifier(string name);
    string GetParameterPlaceholder(int index);
    string FormatBooleanCondition(string operand);
    string CreateContainsPattern(string value) => throw new NotSupportedException("String.Contains is not supported by this SQL dialect.");
    string FormatContainsCondition(string operand, string parameter) => throw new NotSupportedException("String.Contains is not supported by this SQL dialect.");
    DbParameter CreateParameter(QueryParameter parameter);
}
