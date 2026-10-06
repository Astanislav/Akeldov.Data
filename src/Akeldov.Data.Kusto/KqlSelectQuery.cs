using System.Linq.Expressions;
using Kusto.Data.Common;

namespace Akeldov.Data.Kusto;

internal sealed record KqlSelectQuery(string Database, string Text, ClientRequestProperties Properties)
{
    internal static KqlSelectQuery Create<T>(TableMapping<T> mapping, Expression<Func<T, bool>>? predicate = null)
        where T : class, new()
    {
        var text = KqlSyntax.QuoteIdentifier(mapping.Table.Name);
        var properties = new ClientRequestProperties();
        if (predicate is not null)
        {
            var translation = KqlPredicateTranslator<T>.Translate(mapping, predicate);
            if (translation.Parameters.Length > 0)
            {
                var declarations = string.Join(", ", translation.Parameters.Select(parameter => $"{parameter.Name}:{parameter.Type}"));
                text = $"declare query_parameters({declarations});\n{text}";
                foreach (var parameter in translation.Parameters)
                {
                    properties.SetParameter(parameter.Name, parameter.Value);
                }
            }

            text += $" | where {translation.Kql}";
        }

        text += $" | project {string.Join(", ", mapping.ColumnNames.Select(KqlSyntax.QuoteIdentifier))}";
        return new KqlSelectQuery(mapping.Table.Schema, text, properties);
    }
}
