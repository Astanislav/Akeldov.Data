using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Akeldov.Data;

internal sealed class SqlPredicateTranslator<T> where T : class, new()
{
    private readonly TableMapping<T> mapping;
    private readonly ParameterExpression rowParameter;
    private readonly List<SqlParameter> parameters = [];

    private SqlPredicateTranslator(TableMapping<T> mapping, ParameterExpression rowParameter)
    {
        this.mapping = mapping;
        this.rowParameter = rowParameter;
    }

    internal static (string Sql, SqlParameter[] Parameters) Translate(
        TableMapping<T> mapping, Expression<Func<T, bool>> predicate)
    {
        var translator = new SqlPredicateTranslator<T>(mapping, predicate.Parameters[0]);
        var sql = translator.TranslatePredicate(predicate.Body);
        return (sql, translator.parameters.ToArray());
    }

    private string TranslatePredicate(Expression expression)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            {
                var operation = binary.NodeType == ExpressionType.AndAlso ? "AND" : "OR";
                return $"({TranslatePredicate(binary.Left)} {operation} {TranslatePredicate(binary.Right)})";
            }

            return TranslateComparison(binary);
        }

        if (expression is UnaryExpression { NodeType: ExpressionType.Not } negation)
        {
            return $"(NOT {TranslatePredicate(negation.Operand)})";
        }

        if (expression.Type == typeof(bool))
        {
            var operand = TranslateOperand(expression);
            return $"({operand.Sql} = 1)";
        }

        throw Unsupported(expression);
    }

    private string TranslateComparison(BinaryExpression expression)
    {
        var operation = expression.NodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw Unsupported(expression)
        };

        if (expression.Method is { } method && !IsSupportedValueType(method.DeclaringType!))
        {
            throw Unsupported(expression);
        }

        var left = TranslateOperand(expression.Left);
        var right = TranslateOperand(expression.Right);

        if (left.IsNull || right.IsNull)
        {
            if (expression.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
            {
                return "(1 = 0)";
            }

            if (left.IsNull && right.IsNull)
            {
                return expression.NodeType == ExpressionType.Equal ? "(1 = 1)" : "(1 = 0)";
            }

            var operand = left.IsNull ? right : left;
            return $"({operand.Sql} IS {(expression.NodeType == ExpressionType.Equal ? "NULL" : "NOT NULL")})";
        }

        var comparison = $"{left.Sql} {operation} {right.Sql}";
        if (!left.CanBeNull && !right.CanBeNull)
        {
            return $"({comparison})";
        }

        // Make comparisons two-valued so NOT also preserves C# nullable semantics.
        var guards = new List<string>();
        if (left.CanBeNull)
        {
            guards.Add($"{left.Sql} IS NOT NULL");
        }
        if (right.CanBeNull)
        {
            guards.Add($"{right.Sql} IS NOT NULL");
        }
        var guardedComparison = $"({string.Join(" AND ", guards)} AND {comparison})";

        if (expression.NodeType == ExpressionType.Equal && left.CanBeNull && right.CanBeNull)
        {
            return $"({guardedComparison} OR ({left.Sql} IS NULL AND {right.Sql} IS NULL))";
        }

        if (expression.NodeType == ExpressionType.NotEqual)
        {
            if (left.CanBeNull && right.CanBeNull)
            {
                return $"({guardedComparison} OR ({left.Sql} IS NULL AND {right.Sql} IS NOT NULL) OR ({left.Sql} IS NOT NULL AND {right.Sql} IS NULL))";
            }

            var nullableOperand = left.CanBeNull ? left : right;
            return $"({nullableOperand.Sql} IS NULL OR {guardedComparison})";
        }

        return guardedComparison;
    }

    private Operand TranslateOperand(Expression expression)
    {
        expression = UnwrapConversion(expression);

        if (expression is MemberExpression { Member: PropertyInfo property } member
            && member.Expression == rowParameter)
        {
            if (!IsSupportedValueType(property.PropertyType))
            {
                throw Unsupported(expression);
            }

            var sql = TableMapping<T>.QuoteIdentifier(mapping.GetColumnName(property));
            var nullable = !property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null;
            return new Operand(sql, nullable);
        }

        var value = ReadValue(expression);
        if (value is null)
        {
            return new Operand("NULL", true, true);
        }

        value = NormalizeValue(value);
        var parameter = new SqlParameter($"@p{parameters.Count}", value);
        parameters.Add(parameter);
        return new Operand(parameter.ParameterName, false);
    }

    private static object? ReadValue(Expression expression)
    {
        expression = UnwrapConversion(expression);
        if (expression is ConstantExpression constant)
        {
            return constant.Value;
        }

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

        // Never evaluate expressions that reference a row or call arbitrary methods.
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
        var sourceCode = Type.GetTypeCode(source);
        var targets = sourceCode switch
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

    private static object NormalizeValue(object value)
    {
        var type = value.GetType();
        if (type.IsEnum)
        {
            value = Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
        }

        if (!IsSupportedValueType(value.GetType()))
        {
            throw new NotSupportedException($"Predicate value type '{value.GetType()}' is not supported.");
        }

        return value switch
        {
            char character => character.ToString(),
            sbyte number => (short)number,
            ushort number => (int)number,
            uint number => (long)number,
            ulong number => (decimal)number,
            _ => value
        };
    }

    private static bool IsSupportedValueType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type == typeof(string) || type == typeof(bool) || type == typeof(char)
            || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
            || type == typeof(float) || type == typeof(double) || type == typeof(decimal)
            || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan) || type == typeof(byte[]);
    }

    private static NotSupportedException Unsupported(Expression expression)
        => new($"Predicate expression '{expression}' ({expression.NodeType}) is not supported.");

    private readonly record struct Operand(string Sql, bool CanBeNull, bool IsNull = false);
}
