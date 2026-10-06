using System.Globalization;
using System.Linq.Expressions;
using NUnit.Framework;

namespace Akeldov.Data.Kusto.Tests;

[TestFixture]
public class KqlSelectQueryTests
{
    [Test]
    public void UnfilteredQuery_UsesDatabaseAndProjectsOnlyMappedColumns()
    {
        var query = KqlSelectQuery.Create(TableMapping<User>.Create());

        Assert.Multiple(() =>
        {
            Assert.That(query.Database, Is.EqualTo("analytics"));
            Assert.That(query.Text, Is.EqualTo("['users'] | project ['user_id'], ['name'], ['score'], ['other_score'], ['active']"));
        });
    }

    [TestCase(ExpressionType.Equal, "==")]
    [TestCase(ExpressionType.NotEqual, "!=")]
    [TestCase(ExpressionType.GreaterThan, ">")]
    [TestCase(ExpressionType.GreaterThanOrEqual, ">=")]
    [TestCase(ExpressionType.LessThan, "<")]
    [TestCase(ExpressionType.LessThanOrEqual, "<=")]
    public void Comparison_DeclaresAndAttachesTypedParameters(ExpressionType nodeType, string operation)
    {
        var row = Expression.Parameter(typeof(User), "row");
        var comparison = Expression.MakeBinary(nodeType, Expression.Property(row, nameof(User.Id)), Expression.Constant(10));
        var predicate = Expression.Lambda<Func<User, bool>>(comparison, row);
        var query = KqlSelectQuery.Create(TableMapping<User>.Create(), predicate);

        Assert.Multiple(() =>
        {
            Assert.That(query.Text, Does.StartWith("declare query_parameters(akeldov_p0:int);\n['users'] | where "));
            Assert.That(query.Text, Does.Contain($"(['user_id'] {operation} akeldov_p0) | project"));
            Assert.That(query.Properties.Parameters["akeldov_p0"], Is.EqualTo("10"));
        });
    }

    [Test]
    public void BooleanOperators_PreserveGroupingAndCaptureCurrentValues()
    {
        var minimum = 5;
        Expression<Func<User, bool>> predicate = row => (row.Id > minimum || row.Id < 20) && !row.Active;
        var first = Translate(predicate);
        minimum = 9;
        var second = Translate(predicate);

        Assert.Multiple(() =>
        {
            Assert.That(first.Kql, Is.EqualTo("(((['user_id'] > akeldov_p0) or (['user_id'] < akeldov_p1)) and not(coalesce(['active'], false)))"));
            Assert.That(first.Parameters.Select(parameter => parameter.Value), Is.EqualTo(new[] { "5", "20" }));
            Assert.That(second.Parameters[0].Value, Is.EqualTo("9"));
        });
    }

    [Test]
    public void StringParameter_KeepsUntrustedValueOutsideQueryText()
    {
        var name = "'); users | take 1 //\\\n";
        var query = KqlSelectQuery.Create(TableMapping<User>.Create(), row => row.Name == name);

        Assert.Multiple(() =>
        {
            Assert.That(query.Text, Does.Not.Contain(name));
            Assert.That(query.Text, Does.Contain("akeldov_p0:string"));
            Assert.That(query.Properties.Parameters["akeldov_p0"], Is.EqualTo(name));
        });
    }

    [Test]
    public void NullableComparisons_AreTwoValuedUnderNegation()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Translate(row => !(row.Score > 10)).Kql,
                Is.EqualTo("not(coalesce((['score'] > akeldov_p0), false))"));
            Assert.That(Translate(row => row.Score != 10).Kql,
                Is.EqualTo("(isnull(['score']) or coalesce((['score'] != akeldov_p0), false))"));
            Assert.That(Translate(row => row.Score == row.OtherScore).Kql,
                Is.EqualTo("(coalesce((['score'] == ['other_score']), false) or (isnull(['score']) and isnull(['other_score'])))"));
            Assert.That(Translate(row => row.Score != row.OtherScore).Kql,
                Is.EqualTo("(coalesce((['score'] != ['other_score']), false) or (isnull(['score']) and isnotnull(['other_score'])) or (isnotnull(['score']) and isnull(['other_score'])))"));
        });
    }

    [Test]
    public void NullChecks_UseKustoScalarSemantics()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Translate(row => row.Score == null).Kql, Is.EqualTo("isnull(['score'])"));
            Assert.That(Translate(row => row.Score != null).Kql, Is.EqualTo("isnotnull(['score'])"));
            Assert.That(Translate(row => row.Name == null).Kql, Is.EqualTo("false"));
            Assert.That(Translate(row => row.Name != null).Kql, Is.EqualTo("true"));
            Assert.That(Translate(row => row.Name == "").Kql, Is.EqualTo("(['name'] == akeldov_p0)"));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ConstantPredicate_DeclaresBooleanParameter(bool value)
    {
        var query = KqlSelectQuery.Create(TableMapping<User>.Create(), row => value);
        Assert.That(query.Properties.Parameters["akeldov_p0"], Is.EqualTo(value ? "true" : "false"));
        Assert.That(query.Text, Does.Contain("akeldov_p0:bool"));
    }

    [Test]
    public void ParameterNames_DoNotCollideWithColumns()
    {
        var query = KqlSelectQuery.Create(TableMapping<ParameterCollision>.Create(), row => row.Value > 3);
        Assert.That(query.Text, Does.StartWith("declare query_parameters(akeldov_p1:int);"));
        Assert.That(query.Text, Does.Contain("(['akeldov_p0'] > akeldov_p1)"));
    }

    [Test]
    public void IdentifierQuoting_EscapesBackslashQuoteAndControlCharacters()
    {
        Assert.That(KqlSyntax.QuoteIdentifier("a'\\\r\n\t\0]"), Is.EqualTo("['a\\'\\\\\\r\\n\\t\\u0000]']"));
    }

    [Test]
    public void CaseDistinctColumns_ArePreserved()
    {
        var query = KqlSelectQuery.Create(TableMapping<CaseDistinctColumns>.Create());
        Assert.That(query.Text, Does.EndWith("| project ['Value'], ['value']"));
    }

    [Test]
    public void ScalarParameters_UseInvariantValuesAndUtcDates()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var date = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(3));
            var guid = Guid.Parse("931259b1-ec3b-4e56-bafa-c0a0840a0546");
            Assert.Multiple(() =>
            {
                Assert.That(KqlParameter.Create(0, 1.5).Value, Is.EqualTo("1.5"));
                Assert.That(KqlParameter.Create(0, date).Value, Is.EqualTo("datetime(2026-10-07T09:00:00.0000000Z)"));
                Assert.That(KqlParameter.Create(0, date.UtcDateTime).Value, Is.EqualTo("datetime(2026-10-07T09:00:00.0000000Z)"));
                Assert.That(KqlParameter.Create(0, TimeSpan.FromMinutes(2)).Value, Is.EqualTo("timespan(00:02:00)"));
                Assert.That(KqlParameter.Create(0, guid).Value, Is.EqualTo($"guid({guid:D})"));
                Assert.That(KqlParameter.Create(0, State.Active).Value, Is.EqualTo("1"));
                Assert.That(KqlParameter.Create(0, uint.MaxValue).Type, Is.EqualTo("long"));
                Assert.That(KqlParameter.Create(0, date.DateTime).Value, Is.EqualTo("datetime(2026-10-07T12:00:00.0000000Z)"));
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Test]
    public void UnsupportedScalarTypes_RejectLossyOrUnrepresentableValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => KqlParameter.Create(0, 1.1m), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => KqlParameter.Create(0, 'x'), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => KqlParameter.Create(0, new byte[] { 1 }), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => KqlParameter.Create(0, ulong.MaxValue), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => KqlParameter.Create(0, double.NaN), Throws.TypeOf<NotSupportedException>());
        });
    }

    [Test]
    public void UnsupportedPredicates_RejectMethodCallsUnmappedMembersArithmeticAndNarrowing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => Translate(row => row.Name!.StartsWith("A")), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => Translate(row => row.Unmapped == 1), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => Translate(row => row.Id + 1 > 2), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => Translate(row => (short)row.Id == 1), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => Translate(row => row.Score!.Value > 1), Throws.TypeOf<NotSupportedException>());
        });
    }

    private static (string Kql, KqlParameter[] Parameters) Translate(Expression<Func<User, bool>> predicate)
        => KqlPredicateTranslator<User>.Translate(TableMapping<User>.Create(), predicate);

    [Table("analytics", "users")]
    public class User
    {
        [Column("user_id")] public int Id { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("other_score")] public int? OtherScore { get; set; }
        [Column("active")] public bool Active { get; set; }
        public int Unmapped { get; set; }
    }

    [Table("analytics", "values")]
    public class ParameterCollision
    {
        [Column("akeldov_p0")] public int Value { get; set; }
    }

    [Table("analytics", "values")]
    public class CaseDistinctColumns
    {
        [Column("Value")] public int Upper { get; set; }
        [Column("value")] public int Lower { get; set; }
    }

    private enum State { Active = 1 }
}
