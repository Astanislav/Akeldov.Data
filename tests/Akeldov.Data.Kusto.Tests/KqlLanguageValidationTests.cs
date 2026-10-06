using System.Linq.Expressions;
using System.Reflection;
using Kusto.Language;
using Kusto.Language.Symbols;
using NUnit.Framework;

namespace Akeldov.Data.Kusto.Tests;

[TestFixture]
public class KqlLanguageValidationTests
{
    [Test]
    public void GeneratedQueries_PassOfficialKustoSyntaxAndTypeAnalysis()
    {
        Expression<Func<KqlSelectQueryTests.User, bool>>[] predicates =
        [
            row => row.Id >= 1 && row.Id < 20,
            row => row.Name == "Alice" || !row.Active,
            row => !(row.Score > 10),
            row => row.Score == row.OtherScore,
            row => row.Score != row.OtherScore,
            row => row.Score == null,
            row => row.Name == null,
            row => row.Name == "",
            row => true
        ];
        foreach (var predicate in predicates)
        {
            ValidateQuery(predicate);
        }
        ValidateQuery<KqlSelectQueryTests.ParameterCollision>(row => row.Value > 1);
        ValidateQuery<KqlSelectQueryTests.CaseDistinctColumns>(row => row.Upper == row.Lower);
    }

    [Test]
    public void TypedComparisons_PassKustoTypeAnalysis()
    {
        var created = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        var offset = new DateTimeOffset(created);
        var duration = TimeSpan.FromMinutes(2);
        var id = Guid.NewGuid();
        ValidateQuery<TypedRow>(row => row.Created >= created && row.Offset == offset
            && row.Duration < duration && row.ExternalId == id && row.Measurement >= 1.5 && row.Count < uint.MaxValue);
    }

    [Test]
    public void ParameterSerialization_ProducesValidKqlScalarLiterals()
    {
        object[] values =
        [
            "A'\\\n\0", true, (byte)5, (sbyte)-1, (short)-20, ushort.MaxValue, int.MinValue,
            uint.MaxValue, long.MinValue, (ulong)long.MaxValue, 1.5f, 1.5, Guid.NewGuid(),
            new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(2), TimeSpan.FromTicks(-1), TimeSpan.FromDays(2)
        ];
        foreach (var value in values)
        {
            var parameter = KqlParameter.Create(0, value);
            var literal = parameter.Type == "string" ? KqlSyntax.QuoteString(parameter.Value) : parameter.Value;
            var code = KustoCode.ParseAndAnalyze($"print value={literal}");
            Assert.That(code.GetDiagnostics(), Is.Empty, $"Invalid {parameter.Type} literal: {literal}");
        }
    }

    private static void ValidateQuery<T>(Expression<Func<T, bool>> predicate) where T : class, new()
    {
        var mapping = TableMapping<T>.Create();
        var columns = typeof(T).GetProperties()
            .Select(property => (Property: property, Column: property.GetCustomAttribute<ColumnAttribute>()))
            .Where(item => item.Column is not null)
            .Select(item => new ColumnSymbol(item.Column!.Name, ScalarTypes.GetSymbol(KqlParameter.GetScalarType(item.Property.PropertyType))));
        var table = new TableSymbol(mapping.Table.Name, columns);
        var globals = GlobalState.Default.WithDatabase(new DatabaseSymbol(mapping.Table.Schema, table));
        var query = KqlSelectQuery.Create(mapping, predicate);
        var code = KustoCode.ParseAndAnalyze(query.Text, globals);
        Assert.That(code.GetDiagnostics(), Is.Empty, query.Text);
    }

    [Table("analytics", "typed")]
    public class TypedRow
    {
        [Column("created")] public DateTime Created { get; set; }
        [Column("offset")] public DateTimeOffset Offset { get; set; }
        [Column("duration")] public TimeSpan Duration { get; set; }
        [Column("external_id")] public Guid ExternalId { get; set; }
        [Column("measurement")] public double Measurement { get; set; }
        [Column("count")] public long Count { get; set; }
    }
}
