using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.SqlServer.Tests;

[TestFixture]
public class SqlPredicateTranslatorTests
{
    [TestCase(ExpressionType.Equal, "=")]
    [TestCase(ExpressionType.NotEqual, "<>")]
    [TestCase(ExpressionType.GreaterThan, ">")]
    [TestCase(ExpressionType.GreaterThanOrEqual, ">=")]
    [TestCase(ExpressionType.LessThan, "<")]
    [TestCase(ExpressionType.LessThanOrEqual, "<=")]
    public void Translate_Comparison_UsesMappedColumnAndParameter(ExpressionType nodeType, string operation)
    {
        var row = Expression.Parameter(typeof(User), "row");
        var comparison = Expression.MakeBinary(nodeType, Expression.Property(row, nameof(User.Id)), Expression.Constant(10));
        var predicate = Expression.Lambda<Func<User, bool>>(comparison, row);

        var result = Translate(predicate);

        Assert.Multiple(() =>
        {
            Assert.That(result.Sql, Is.EqualTo($"([user_id] {operation} @p0)"));
            Assert.That(result.Parameters, Has.Length.EqualTo(1));
            Assert.That(result.Parameters[0].ParameterName, Is.EqualTo("@p0"));
            Assert.That(result.Parameters[0].Value, Is.EqualTo(10));
        });
    }

    [Test]
    public void Translate_AndOrNot_PreservesGroupingAndParameterOrder()
    {
        var minId = 5;
        var maxId = 20;

        var result = Translate(user => (user.Id > minId || user.Id < maxId) && !user.Active);

        Assert.Multiple(() =>
        {
            Assert.That(result.Sql,
                Is.EqualTo("((([user_id] > @p0) OR ([user_id] < @p1)) AND (NOT ([is_active] = 1)))"));
            Assert.That(result.Parameters.Select(parameter => parameter.Value), Is.EqualTo(new[] { minId, maxId }));
        });
    }

    [Test]
    public void Translate_CapturedObjectProperty_ReadsCurrentValueAtEachTranslation()
    {
        var settings = new Settings { MinimumId = 5 };
        Expression<Func<User, bool>> predicate = user => user.Id >= settings.MinimumId;

        var first = Translate(predicate);
        settings.MinimumId = 9;
        var second = Translate(predicate);

        Assert.Multiple(() =>
        {
            Assert.That(first.Parameters[0].Value, Is.EqualTo(5));
            Assert.That(second.Parameters[0].Value, Is.EqualTo(9));
            Assert.That(second.Parameters[0].ParameterName, Is.EqualTo("@p0"));
        });
    }

    [Test]
    public void Translate_StringValue_RemainsOutsideSqlText()
    {
        var name = "Robert'); DROP TABLE users;--";

        var result = Translate(user => user.Name == name);

        Assert.Multiple(() =>
        {
            Assert.That(result.Sql, Is.EqualTo("([display_name] IS NOT NULL AND [display_name] = @p0)"));
            Assert.That(result.Sql, Does.Not.Contain(name));
            Assert.That(result.Parameters[0].Value, Is.EqualTo(name));
            Assert.That(result.Parameters[0].SqlDbType, Is.EqualTo(SqlDbType.NVarChar));
        });
    }

    [Test]
    public void Translate_LiteralNull_UsesIsNullAndIsNotNullWithoutParameters()
    {
        var result = Translate(user => user.Name == null || null != user.Name);

        Assert.Multiple(() =>
        {
            Assert.That(result.Sql, Is.EqualTo("(([display_name] IS NULL) OR ([display_name] IS NOT NULL))"));
            Assert.That(result.Parameters, Is.Empty);
        });
    }

    [Test]
    public void Translate_CapturedNull_UsesIsNull()
    {
        int? score = null;

        var result = Translate(user => user.Score == score);

        Assert.That(result.Sql, Is.EqualTo("([score] IS NULL)"));
        Assert.That(result.Parameters, Is.Empty);
    }

    [Test]
    public void Translate_CapturedNullableValue_UsesUnderlyingParameterValue()
    {
        int? score = 10;

        var result = Translate(user => user.Score == score);

        Assert.That(result.Parameters[0].Value, Is.EqualTo(10));
        Assert.That(result.Parameters[0].SqlDbType, Is.EqualTo(SqlDbType.Int));
    }

    [Test]
    public void Translate_TypedConstants_ParameterizesEnumGuidAndDate()
    {
        var state = UserState.Active;
        var id = Guid.NewGuid();
        var date = new DateTime(2026, 1, 1);

        var result = Translate(user => user.State == state && user.ExternalId == id && user.Created >= date);

        Assert.Multiple(() =>
        {
            Assert.That(result.Parameters, Has.Length.EqualTo(3));
            Assert.That(result.Parameters[0].Value, Is.EqualTo((int)state));
            Assert.That(result.Parameters[1].Value, Is.EqualTo(id));
            Assert.That(result.Parameters[1].SqlDbType, Is.EqualTo(SqlDbType.UniqueIdentifier));
            Assert.That(result.Parameters[2].Value, Is.EqualTo(date));
        });
    }

    [Test]
    public void Translate_WideningIntegerConversion_SupportsLongComparison()
    {
        var minimum = (long)int.MaxValue + 1;

        var result = Translate(user => user.Id >= minimum);

        Assert.That(result.Sql, Is.EqualTo("([user_id] >= @p0)"));
        Assert.That(result.Parameters[0].Value, Is.EqualTo(minimum));
        Assert.That(result.Parameters[0].SqlDbType, Is.EqualTo(SqlDbType.BigInt));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Translate_ConstantPredicate_ParameterizesBoolean(bool value)
    {
        var result = Translate(user => value);

        Assert.That(result.Sql, Is.EqualTo("(@p0 = 1)"));
        Assert.That(result.Parameters[0].Value, Is.EqualTo(value));
        Assert.That(result.Parameters[0].SqlDbType, Is.EqualTo(SqlDbType.Bit));
    }

    [Test]
    public void Translate_ColumnNameWithClosingBracket_EscapesIdentifier()
    {
        var result = SqlPredicateTranslator<EscapedRow>.Translate(
            TableMapping<EscapedRow>.Create(), row => row.Id > 1, SqlServerDialect.Instance);

        Assert.That(result.Sql, Is.EqualTo("([user]]id] > @p0)"));
    }

    [Test]
    public void Translate_InheritedMappedProperty_UsesBaseColumnAttribute()
    {
        var result = SqlPredicateTranslator<DerivedRow>.Translate(
            TableMapping<DerivedRow>.Create(), row => row.Id > 1, SqlServerDialect.Instance);

        Assert.That(result.Sql, Is.EqualTo("([base_id] > @p0)"));
        Assert.That(result.Parameters[0].Value, Is.EqualTo(1));
    }

    [TestCaseSource(nameof(UnsupportedPredicates))]
    public void Translate_UnsupportedExpression_Throws(Expression<Func<User, bool>> predicate)
    {
        Assert.That(() => Translate(predicate), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Translate_CapturedDelegateInvocation_ThrowsWithoutExecutingDelegate()
    {
        var wasCalled = false;
        Func<int> readValue = () =>
        {
            wasCalled = true;
            return 1;
        };

        Assert.That(() => Translate(user => user.Id == readValue()), Throws.TypeOf<NotSupportedException>());
        Assert.That(wasCalled, Is.False);
    }

    [TestCaseSource(nameof(NullablePredicates))]
    public void Translate_NullableComparison_MatchesCSharpForEveryPairOfValues(Expression<Func<User, bool>> predicate)
    {
        var result = Translate(predicate);
        // DataTable evaluates this SQL subset, including NULL, AND, OR, and NOT.
        // Compare its result with the original predicate across all null/value pairs.
        using var table = new DataTable();
        table.Columns.Add("user_id", typeof(int));
        table.Columns.Add("score", typeof(int));
        table.Columns.Add("other_score", typeof(int));
        var users = new List<User>();
        int?[] values = [null, -1, 0, 1];
        foreach (var score in values)
        {
            foreach (var otherScore in values)
            {
                var user = new User { Id = users.Count + 1, Score = score, OtherScore = otherScore };
                users.Add(user);
                table.Rows.Add(user.Id, (object?)score ?? DBNull.Value, (object?)otherScore ?? DBNull.Value);
            }
        }

        var filter = result.Sql;
        foreach (var parameter in result.Parameters.Reverse())
        {
            filter = filter.Replace(parameter.ParameterName, Convert.ToString(parameter.Value, CultureInfo.InvariantCulture));
        }

        var actualIds = table.Select(filter).Select(row => (int)row["user_id"]);
        var expectedIds = users.Where(predicate.Compile()).Select(user => user.Id);

        Assert.That(actualIds, Is.EquivalentTo(expectedIds));
    }

    private static (string Sql, SqlParameter[] Parameters) Translate(Expression<Func<User, bool>> predicate)
    {
        var result = SqlPredicateTranslator<User>.Translate(TableMapping<User>.Create(), predicate, SqlServerDialect.Instance);
        return (result.Sql, result.Parameters.Select(parameter => (SqlParameter)SqlServerDialect.Instance.CreateParameter(parameter)).ToArray());
    }

    private static IEnumerable<TestCaseData> UnsupportedPredicates()
    {
        Expression<Func<User, bool>>[] predicates =
        [
            user => user.Name!.StartsWith("A"),
            user => user.Id + 1 > 5,
            user => user.Unmapped == 1,
            user => user.Name!.Length > 3,
            user => (byte)user.Id == 1,
            user => user.Score!.Value > 1,
            user => user.Active & true
        ];
        return predicates.Select(predicate => new TestCaseData(predicate).SetName($"Unsupported: {predicate}"));
    }

    private static IEnumerable<TestCaseData> NullablePredicates()
    {
        Expression<Func<User, bool>>[] predicates =
        [
            user => user.Score == 1,
            user => user.Score != 1,
            user => user.Score > 0,
            user => user.Score >= 0,
            user => user.Score < 0,
            user => user.Score <= 0,
            user => !(user.Score == 1),
            user => !(user.Score != 1),
            user => !(user.Score > 0),
            user => !(user.Score <= 0),
            user => user.Score == user.OtherScore,
            user => user.Score != user.OtherScore,
            user => !(user.Score == user.OtherScore),
            user => !(user.Score != user.OtherScore),
            user => user.Score > user.OtherScore,
            user => !(user.Score >= user.OtherScore),
            user => user.Score == user.OtherScore || user.Score == null,
            user => user.Score != null && !(user.Score < user.OtherScore)
        ];
        return predicates.Select(predicate => new TestCaseData(predicate).SetName($"Nullable: {predicate}"));
    }

    [Table("dbo", "users")]
    public class User
    {
        [Column("user_id")]
        public int Id { get; set; }

        [Column("display_name")]
        public string? Name { get; set; }

        [Column("score")]
        public int? Score { get; set; }

        [Column("other_score")]
        public int? OtherScore { get; set; }

        [Column("is_active")]
        public bool Active { get; set; }

        [Column("state")]
        public UserState State { get; set; }

        [Column("external_id")]
        public Guid ExternalId { get; set; }

        [Column("created")]
        public DateTime Created { get; set; }

        public int Unmapped { get; set; }
    }

    [Table("dbo", "users")]
    public class EscapedRow
    {
        [Column("user]id")]
        public int Id { get; set; }
    }

    public class Settings
    {
        public int MinimumId { get; set; }
    }

    public class BaseRow
    {
        [Column("base_id")]
        public int Id { get; set; }
    }

    [Table("dbo", "users")]
    public class DerivedRow : BaseRow
    {
    }

    public enum UserState
    {
        Inactive,
        Active
    }
}
