using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.SqlServer.Tests;

[TestFixture]
public class SqlPredicateTranslatorTests
{
    [TestCase(false, "LOWER")]
    [TestCase(true, "UPPER")]
    public void Translate_StringCase_UsesSqlFunctionAndCapturedParameter(bool upper, string function)
    {
        var value = "Robert'); DROP TABLE users;--";
        Expression<Func<User, bool>> predicate = upper
            ? user => user.Name!.ToUpper() == value
            : user => user.Name!.ToLower() == value;
        var result = Translate(predicate);

        Assert.That(result.Sql, Is.EqualTo($"({function}([display_name]) IS NOT NULL AND {function}([display_name]) = @p0)"));
        Assert.That(result.Parameters, Has.Length.EqualTo(1));
        Assert.That(result.Parameters[0].Value, Is.EqualTo(value));
        Assert.That(result.Parameters[0].SqlDbType, Is.EqualTo(SqlDbType.NVarChar));
        value = "changed";
        Assert.That(Translate(predicate).Parameters[0].Value, Is.EqualTo(value));
    }

    [Test]
    public void Translate_StringCase_NullComparisonUsesIsNull()
    {
        var result = Translate(user => user.Name!.ToLower() == null || user.Name!.ToUpper() != null);
        Assert.That(result.Sql, Is.EqualTo("((LOWER([display_name]) IS NULL) OR (UPPER([display_name]) IS NOT NULL))"));
        Assert.That(result.Parameters, Is.Empty);
    }

    [Test]
    public void Translate_StringCase_ComposesWithNegationAndReversedComparison()
    {
        var result = Translate(user => !(user.Name!.ToLower() == "abc") && "XYZ" == user.Name!.ToUpper());
        Assert.That(result.Sql, Is.EqualTo("((NOT (LOWER([display_name]) IS NOT NULL AND LOWER([display_name]) = @p0)) AND (UPPER([display_name]) IS NOT NULL AND @p1 = UPPER([display_name])))"));
        Assert.That(result.Parameters.Select(parameter => parameter.Value), Is.EqualTo(new[] { "abc", "XYZ" }));
    }

    [Test]
    public void Translate_StringCase_RejectsCultureOverloadsAndUnmappedProperties()
    {
        Assert.That(() => Translate(user => user.Name!.ToLower(CultureInfo.InvariantCulture) == "abc"), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Translate(user => user.Name!.ToUpper(CultureInfo.InvariantCulture) == "ABC"), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => SqlPredicateTranslator<SqlServerDialectTests.Row>.Translate(
            TableMapping<SqlServerDialectTests.Row>.Create(), row => row.Unmapped!.ToLower() == "abc", SqlServerDialect.Instance),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Translate_CollectionContains_ParameterizesArraysListsAndInlineArrays()
    {
        int[] ids = [1, 3, 7];
        var list = ids.ToList();
        ICollection<int> collection = list;
        IEnumerable<int> enumerable = ids;
        Expression<Func<User, bool>>[] predicates =
        [
            user => ids.Contains(user.Id),
            user => list.Contains(user.Id),
            user => collection.Contains(user.Id),
            user => enumerable.Contains(user.Id),
            user => new[] { 1, 3, 7 }.Contains(user.Id)
        ];
        foreach (var predicate in predicates)
        {
            var result = Translate(predicate);
            Assert.That(result.Sql, Is.EqualTo("([user_id] IN (@p0, @p1, @p2))"));
            Assert.That(result.Parameters.Select(parameter => parameter.Value), Is.EqualTo(ids));
        }
    }

    [Test]
    public void Translate_CollectionContains_ComposesAndReadsCurrentValues()
    {
        var ids = new List<int> { 1, 2 };
        Expression<Func<User, bool>> predicate = user => user.Id > 0 && !ids.Contains(user.Id);
        var first = Translate(predicate);
        Assert.That(first.Sql, Is.EqualTo("(([user_id] > @p0) AND (NOT ([user_id] IN (@p1, @p2))))"));
        Assert.That(first.Parameters.Select(parameter => parameter.Value), Is.EqualTo(new[] { 0, 1, 2 }));
        ids.Clear();
        var empty = Translate(predicate);
        Assert.That(empty.Sql, Is.EqualTo("(([user_id] > @p0) AND (NOT (1 = 0)))"));
        Assert.That(empty.Parameters, Has.Length.EqualTo(1));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Translate_CollectionContainsNullable_MatchesCSharpIncludingNegation(bool includeNull, bool empty)
    {
        int?[] scores = empty ? [] : includeNull ? [null, 1] : [1, 2];
        using var table = new DataTable();
        table.Columns.Add("score", typeof(int));
        foreach (int? score in new int?[] { null, 0, 1, 2 }) table.Rows.Add((object?)score ?? DBNull.Value);

        foreach (var negate in new[] { false, true })
        {
            Expression<Func<User, bool>> predicate = negate
                ? user => !scores.Contains(user.Score)
                : user => scores.Contains(user.Score);
            var result = Translate(predicate);
            var filter = result.Sql;
            foreach (var parameter in result.Parameters.Reverse())
                filter = filter.Replace(parameter.ParameterName, Convert.ToString(parameter.Value, CultureInfo.InvariantCulture));
            var actual = table.Select(filter).Select(row => row.Field<int?>("score"));
            var expected = new int?[] { null, 0, 1, 2 }.Where(score => negate != scores.Contains(score));
            Assert.That(actual, Is.EquivalentTo(expected));
        }
    }

    [Test]
    public void Translate_CollectionContainsNullOnly_UsesIsNull()
    {
        string?[] names = [null];
        var result = Translate(user => names.Contains(user.Name));
        Assert.That(result.Sql, Is.EqualTo("([display_name] IS NULL)"));
        Assert.That(result.Parameters, Is.Empty);
    }

    [Test]
    public void Translate_CollectionContainsStrings_KeepsValuesOutsideSql()
    {
        string[] names = ["Robert'); DROP TABLE users;--", "Alice"];
        var result = Translate(user => names.Contains(user.Name));
        Assert.That(result.Sql, Is.EqualTo("([display_name] IS NOT NULL AND [display_name] IN (@p0, @p1))"));
        Assert.That(result.Parameters.Select(parameter => parameter.Value), Is.EqualTo(names));
    }

    [Test]
    public void Translate_CollectionContains_RejectsNullAndDeferredSourcesAndComparer()
    {
        int[] ids = null!;
        Assert.That(() => Translate(user => ids.Contains(user.Id)), Throws.ArgumentNullException);
        IEnumerable<int> deferred = Enumerable.Range(1, 3);
        Assert.That(() => Translate(user => deferred.Contains(user.Id)), Throws.TypeOf<NotSupportedException>());
        ids = [1, 2];
        Assert.That(() => Translate(user => ids.Contains(user.Id, EqualityComparer<int>.Default)), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Translate_IsNullOrEmpty_ChecksNullAndExactEmptyWithoutParameters()
    {
        var result = Translate(user => string.IsNullOrEmpty(user.Name));

        // DATALENGTH preserves spaces, unlike LEN or comparison with an empty string.
        Assert.That(result.Sql, Is.EqualTo("([display_name] IS NULL OR DATALENGTH([display_name]) = 0)"));
        Assert.That(result.Parameters, Is.Empty);
    }

    [Test]
    public void Translate_IsNullOrEmpty_ComposesWithNegationAndOtherFilters()
    {
        var result = Translate(user => !string.IsNullOrEmpty(user.Name) && (user.Id > 5 || user.Name!.StartsWith("abc")));

        Assert.That(result.Sql, Is.EqualTo("((NOT ([display_name] IS NULL OR DATALENGTH([display_name]) = 0)) AND (([user_id] > @p0) OR ([display_name] LIKE @p1 ESCAPE '~')))"));
        Assert.That(result.Parameters.Select(parameter => parameter.Value), Is.EqualTo(new object[] { 5, "abc%" }));
    }

    [Test]
    public void Translate_IsNullOrEmpty_UnmappedPropertyThrows()
    {
        Assert.That(() => SqlPredicateTranslator<SqlServerDialectTests.Row>.Translate(
            TableMapping<SqlServerDialectTests.Row>.Create(), row => string.IsNullOrEmpty(row.Unmapped), SqlServerDialect.Instance),
            Throws.TypeOf<NotSupportedException>());
    }

    [TestCase("abc", "abc%", "%abc")]
    [TestCase("", "%", "%")]
    [TestCase("50%_[x]~", "50~%~_~[x]~~%", "%50~%~_~[x]~~")]
    [TestCase("O'Brien", "O'Brien%", "%O'Brien")]
    public void Translate_StartsWithAndEndsWith_UseEscapedParameters(string value, string prefixPattern, string suffixPattern)
    {
        var prefix = Translate(user => user.Name!.StartsWith(value));
        var suffix = Translate(user => user.Name!.EndsWith(value));

        foreach (var result in new[] { prefix, suffix })
        {
            Assert.That(result.Sql, Is.EqualTo("([display_name] LIKE @p0 ESCAPE '~')"));
            Assert.That(result.Parameters, Has.Length.EqualTo(1));
            Assert.That(result.Parameters[0].SqlDbType, Is.EqualTo(SqlDbType.NVarChar));
        }
        Assert.That(prefix.Parameters[0].Value, Is.EqualTo(prefixPattern));
        Assert.That(suffix.Parameters[0].Value, Is.EqualTo(suffixPattern));
    }

    [Test]
    public void Translate_StartsWithAndEndsWith_ComposeWithLogicalOperators()
    {
        var result = Translate(user => user.Id > 5 && (!user.Name!.StartsWith("abc") || user.Name.EndsWith("xyz")));

        Assert.That(result.Sql, Is.EqualTo("(([user_id] > @p0) AND ((NOT ([display_name] LIKE @p1 ESCAPE '~')) OR ([display_name] LIKE @p2 ESCAPE '~')))"));
        Assert.That(result.Parameters.Select(parameter => parameter.Value), Is.EqualTo(new object[] { 5, "abc%", "%xyz" }));
    }

    [Test]
    public void Translate_StartsWithAndEndsWithNull_Throw()
    {
        string value = null!;
        Assert.That(() => Translate(user => user.Name!.StartsWith(value)), Throws.ArgumentNullException);
        Assert.That(() => Translate(user => user.Name!.EndsWith(value)), Throws.ArgumentNullException);
    }

    [TestCase("Alice", "%Alice%")]
    [TestCase("", "%%")]
    [TestCase("50%_[x]~", "%50~%~_~[x]~~%")]
    [TestCase("Robert'); DROP TABLE users;--", "%Robert'); DROP TABLE users;--%")]
    public void Translate_Contains_UsesEscapedParameterizedPattern(string value, string pattern)
    {
        var result = Translate(user => user.Name!.Contains(value));

        Assert.That(result.Sql, Is.EqualTo("([display_name] LIKE @p0 ESCAPE '~')"));
        Assert.That(result.Parameters, Has.Length.EqualTo(1));
        Assert.That(result.Parameters[0].Value, Is.EqualTo(pattern));
        Assert.That(result.Parameters[0].SqlDbType, Is.EqualTo(SqlDbType.NVarChar));
    }

    [Test]
    public void Translate_ContainsLiteralWithLogicalOperators_PreservesGroupingAndParameterOrder()
    {
        var result = Translate(user => user.Id > 5 && (!user.Name!.Contains("Alice") || user.Name.Contains("Bob")));

        Assert.That(result.Sql, Is.EqualTo("(([user_id] > @p0) AND ((NOT ([display_name] LIKE @p1 ESCAPE '~')) OR ([display_name] LIKE @p2 ESCAPE '~')))"));
        Assert.That(result.Parameters.Select(parameter => parameter.Value), Is.EqualTo(new object[] { 5, "%Alice%", "%Bob%" }));
    }

    [Test]
    public void Translate_ContainsNull_Throws()
    {
        string value = null!;
        Assert.That(() => Translate(user => user.Name!.Contains(value)), Throws.ArgumentNullException);
    }

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
            user => user.Name!.StartsWith("A", StringComparison.OrdinalIgnoreCase),
            user => user.Name!.EndsWith("A", StringComparison.OrdinalIgnoreCase),
            user => user.Name!.StartsWith('A'),
            user => user.Name!.EndsWith('A'),
            user => user.Name!.StartsWith(user.Name),
            user => user.Name!.EndsWith(user.Name),
            user => user.Name!.Contains("A", StringComparison.OrdinalIgnoreCase),
            user => user.Name!.Contains('A'),
            user => user.Name!.Contains(user.Name),
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
