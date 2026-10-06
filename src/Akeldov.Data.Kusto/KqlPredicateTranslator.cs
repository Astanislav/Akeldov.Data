using System.Linq.Expressions;
using System.Reflection;

namespace Akeldov.Data.Kusto;

internal sealed class KqlPredicateTranslator<T> where T : class, new()
{
    private readonly TableMapping<T> mapping;
    private readonly ParameterExpression rowParameter;
    private readonly List<KqlParameter> parameters = [];
    private readonly HashSet<string> columnNames;
    private int parameterIndex;

    private KqlPredicateTranslator(TableMapping<T> mapping, ParameterExpression rowParameter)
    {
        this.mapping = mapping;
        this.rowParameter = rowParameter;
        columnNames = new HashSet<string>(mapping.ColumnNames, StringComparer.Ordinal);
    }

    internal static (string Kql, KqlParameter[] Parameters) Translate(
        TableMapping<T> mapping, Expression<Func<T, bool>> predicate)
    {
        var translator = new KqlPredicateTranslator<T>(mapping, predicate.Parameters[0]);
        var kql = translator.TranslatePredicate(predicate.Body);
        return (kql, translator.parameters.ToArray());
    }

    private string TranslatePredicate(Expression expression)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            {
                var operation = binary.NodeType == ExpressionType.AndAlso ? "and" : "or";
                return $"({TranslatePredicate(binary.Left)} {operation} {TranslatePredicate(binary.Right)})";
            }

            return TranslateComparison(binary);
        }

        if (expression is UnaryExpression { NodeType: ExpressionType.Not } negation)
        {
            return $"not({TranslatePredicate(negation.Operand)})";
        }

        if (expression.Type == typeof(bool))
        {
            return $"coalesce({TranslateOperand(expression).Kql}, false)";
        }

        throw Unsupported(expression);
    }

    private string TranslateComparison(BinaryExpression expression)
    {
        var operation = expression.NodeType switch
        {
            ExpressionType.Equal => "==",
            ExpressionType.NotEqual => "!=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw Unsupported(expression)
        };

        if (expression.Method is { } method)
        {
            // Only built-in scalar comparisons are supported, never arbitrary operator overloads.
            _ = KqlParameter.GetScalarType(method.DeclaringType!);
        }

        var left = TranslateOperand(expression.Left);
        var right = TranslateOperand(expression.Right);

        if (left.IsNull || right.IsNull)
        {
            if (expression.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual)) return "false";
            if (left.IsNull && right.IsNull) return expression.NodeType == ExpressionType.Equal ? "true" : "false";

            var operand = left.IsNull ? right : left;
            // Kusto strings cannot be null. Do not silently treat an empty string as a C# null.
            if (operand.IsString) return expression.NodeType == ExpressionType.Equal ? "false" : "true";
            return $"{(expression.NodeType == ExpressionType.Equal ? "isnull" : "isnotnull")}({operand.Kql})";
        }

        var comparison = $"({left.Kql} {operation} {right.Kql})";
        if (!left.CanBeNull && !right.CanBeNull) return comparison;

        // Force two-valued comparisons so negation preserves C# nullable semantics.
        var guarded = $"coalesce({comparison}, false)";
        if (expression.NodeType == ExpressionType.Equal && left.CanBeNull && right.CanBeNull)
        {
            return $"({guarded} or (isnull({left.Kql}) and isnull({right.Kql})))";
        }

        if (expression.NodeType == ExpressionType.NotEqual)
        {
            if (left.CanBeNull && right.CanBeNull)
            {
                return $"({guarded} or (isnull({left.Kql}) and isnotnull({right.Kql})) or (isnotnull({left.Kql}) and isnull({right.Kql})))";
            }

            return $"(isnull({(left.CanBeNull ? left : right).Kql}) or {guarded})";
        }

        return guarded;
    }

    private Operand TranslateOperand(Expression expression)
    {
        expression = UnwrapConversion(expression);
        if (expression is MemberExpression { Member: PropertyInfo property } member && member.Expression == rowParameter)
        {
            var type = KqlParameter.GetScalarType(property.PropertyType);
            var kql = KqlSyntax.QuoteIdentifier(mapping.GetColumnName(property));
            var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null;
            return new Operand(kql, nullable, IsString: type == "string");
        }

        var value = ReadValue(expression);
        if (value is null) return new Operand("", true, IsNull: true);

        while (columnNames.Contains($"akeldov_p{parameterIndex}")) parameterIndex++;
        var parameter = KqlParameter.Create(parameterIndex++, value);
        parameters.Add(parameter);
        // Query parameter names must not collide with mapped columns in Kusto's row scope.
        return new Operand(parameter.Name, false, IsString: parameter.Type == "string");
    }

    private static object? ReadValue(Expression expression)
    {
        expression = UnwrapConversion(expression);
        if (expression is ConstantExpression constant) return constant.Value;
        if (expression is MemberExpression member)
        {
            var target = member.Expression is null ? null : ReadValue(member.Expression);
            return member.Member switch
            {
                FieldInfo field => field.GetValue(target),
                PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(target),
                _ => throw Unsupported(expression)
            };
        }

        throw Unsupported(expression);
    }

    private static Expression UnwrapConversion(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
        {
            var sourceType = Nullable.GetUnderlyingType(conversion.Operand.Type) ?? conversion.Operand.Type;
            var targetType = Nullable.GetUnderlyingType(conversion.Type) ?? conversion.Type;
            var removesNullability = Nullable.GetUnderlyingType(conversion.Operand.Type) is not null
                && Nullable.GetUnderlyingType(conversion.Type) is null;
            if (conversion.Method is not null || removesNullability
                || !(sourceType == targetType || sourceType.IsEnum && Enum.GetUnderlyingType(sourceType) == targetType
                    || IsWideningIntegerConversion(sourceType, targetType)))
            {
                throw Unsupported(expression);
            }

            expression = conversion.Operand;
        }

        return expression;
    }

    private static bool IsWideningIntegerConversion(Type source, Type target)
    {
        var targets = Type.GetTypeCode(source) switch
        {
            TypeCode.SByte => new[] { TypeCode.Int16, TypeCode.Int32, TypeCode.Int64 },
            TypeCode.Byte => new[] { TypeCode.Int16, TypeCode.UInt16, TypeCode.Int32, TypeCode.UInt32, TypeCode.Int64, TypeCode.UInt64 },
            TypeCode.Int16 => new[] { TypeCode.Int32, TypeCode.Int64 },
            TypeCode.UInt16 => new[] { TypeCode.Int32, TypeCode.UInt32, TypeCode.Int64, TypeCode.UInt64 },
            TypeCode.Int32 => new[] { TypeCode.Int64 },
            TypeCode.UInt32 => new[] { TypeCode.Int64, TypeCode.UInt64 },
            _ => Array.Empty<TypeCode>()
        };
        return targets.Contains(Type.GetTypeCode(target));
    }

    private static NotSupportedException Unsupported(Expression expression)
        => new($"Predicate expression '{expression}' ({expression.NodeType}) is not supported by KQL.");

    private readonly record struct Operand(string Kql, bool CanBeNull, bool IsNull = false, bool IsString = false);
}
