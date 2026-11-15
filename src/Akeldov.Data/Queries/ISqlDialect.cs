using System.Data.Common;

namespace Akeldov.Data;

internal interface ISqlDialect
{
    StringComparer ColumnNameComparer { get; }
    bool SupportsCollectionContains => false;
    bool LoadsReferenceCollections => false;
    string QuoteIdentifier(string name);
    string GetParameterPlaceholder(int index);
    string FormatBooleanCondition(string operand);
    string FormatStringFunction(string operand, string methodName) => throw new NotSupportedException($"String.{methodName} is not supported by this SQL dialect.");
    string FormatIsNullOrEmptyCondition(string operand) => throw new NotSupportedException("String.IsNullOrEmpty is not supported by this SQL dialect.");
    string CreateStringPattern(string value, string methodName) => throw new NotSupportedException($"String.{methodName} is not supported by this SQL dialect.");
    string FormatLikeCondition(string operand, string parameter) => throw new NotSupportedException("String matching is not supported by this SQL dialect.");
    DbParameter CreateParameter(QueryParameter parameter);
}
