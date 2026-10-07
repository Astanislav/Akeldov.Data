using System.Data;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.ADO.Parameters;
using NUnit.Framework;

namespace Akeldov.Data.ClickHouse.Tests;

[TestFixture]
public class ClickHouseDialectTests
{
    [Test]
    public void CreateCommand_QuotesNamesAndUsesTypedParameters()
    {
        using var connection = new ClickHouseConnection();
        var minimum = 5;
        using var command = SelectExecutor.CreateSelectCommand(connection, TableMapping<Row>.Create(),
            ClickHouseDialect.Instance, row => row.Id >= minimum && !row.Active);
        Assert.Multiple(() =>
        {
            Assert.That(command.CommandText,
                Is.EqualTo("SELECT `user_id`, `is_active`, `score`, `other_score` FROM `analytics`.`users` WHERE ((`user_id` >= @p0) AND (NOT (`is_active` = 1)))"));
            Assert.That(command.Parameters, Has.Count.EqualTo(1));
            var parameter = (ClickHouseDbParameter)command.Parameters[0];
            Assert.That(parameter.ParameterName, Is.EqualTo("p0"));
            Assert.That(parameter.Value, Is.EqualTo(minimum));
            Assert.That(parameter.QueryForm, Is.EqualTo("{p0:Int32}"));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void CreateCommand_WithoutPredicate_HasOnlyMappedColumns()
    {
        using var connection = new ClickHouseConnection();
        using var command = SelectExecutor.CreateSelectCommand(connection, TableMapping<CaseRow>.Create(), ClickHouseDialect.Instance);
        Assert.That(command.CommandText, Is.EqualTo("SELECT `id`, `ID` FROM `analytics`.`case_rows`"));
        Assert.That(command.Parameters, Is.Empty);
    }

    [TestCase("ordinary", "`ordinary`")]
    [TestCase("odd`name", "`odd\\`name`")]
    [TestCase("slash\\`name", "`slash\\\\\\`name`")]
    public void QuoteIdentifier_EscapesBackticksAndBackslashes(string name, string expected)
        => Assert.That(ClickHouseDialect.Instance.QuoteIdentifier(name), Is.EqualTo(expected));

    private static IEnumerable<TestCaseData> ScalarParameters()
    {
        yield return new TestCaseData(true, "Bool");
        yield return new TestCaseData((byte)255, "UInt8");
        yield return new TestCaseData((short)-2, "Int16");
        yield return new TestCaseData(42, "Int32");
        yield return new TestCaseData(long.MaxValue, "Int64");
        yield return new TestCaseData(1.5f, "Float32");
        yield return new TestCaseData(1.5d, "Float64");
        yield return new TestCaseData("Ольга'; --", "String");
        yield return new TestCaseData(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), "UUID");
    }

    [TestCaseSource(nameof(ScalarParameters))]
    public void CreateParameter_UsesExplicitClickHouseTypes(object value, string type)
    {
        var parameter = (ClickHouseDbParameter)ClickHouseDialect.Instance.CreateParameter(new QueryParameter("@p0", value));
        Assert.That(parameter.ClickHouseType, Is.EqualTo(type));
        Assert.That(parameter.QueryForm, Is.EqualTo($"{{p0:{type}}}"));
        Assert.That(parameter.Value, Is.EqualTo(value));
    }

    [TestCase("1", 0)]
    [TestCase("1.2300", 4)]
    [TestCase("0.0000000000000000000000000001", 28)]
    [TestCase("79228162514264337593543950335", 0)]
    public void CreateParameter_Decimal_PreservesScaleAndPrecision(string text, int scale)
    {
        var value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var parameter = (ClickHouseDbParameter)ClickHouseDialect.Instance.CreateParameter(new QueryParameter("@p0", value));
        Assert.That(parameter.ClickHouseType, Is.EqualTo($"Decimal(38, {scale})"));
        Assert.That(parameter.Value, Is.EqualTo(value));
    }

    [TestCase(DateTimeKind.Unspecified)]
    [TestCase(DateTimeKind.Utc)]
    [TestCase(DateTimeKind.Local)]
    public void CreateParameter_DateTime_PreservesTicksAndNormalizesToUtc(DateTimeKind kind)
    {
        var value = new DateTime(2026, 10, 8, 12, 0, 0, kind).AddTicks(1234567);
        var expected = kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        var parameter = (ClickHouseDbParameter)ClickHouseDialect.Instance.CreateParameter(new QueryParameter("@p0", value));
        Assert.That(parameter.ClickHouseType, Is.EqualTo("DateTime64(7, 'UTC')"));
        Assert.That(parameter.Value, Is.EqualTo(expected));
        Assert.That(((DateTime)parameter.Value).Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void CreateParameter_DateTimeOffset_PreservesInstant()
    {
        var value = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.FromHours(3)).AddTicks(1234567);
        var parameter = (ClickHouseDbParameter)ClickHouseDialect.Instance.CreateParameter(new QueryParameter("@p0", value));
        Assert.That(parameter.Value, Is.EqualTo(value.UtcDateTime));
        Assert.That(parameter.ClickHouseType, Is.EqualTo("DateTime64(7, 'UTC')"));
    }

    [TestCase(float.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void CreateParameter_NonFiniteNumber_Throws(object value)
        => Assert.That(() => ClickHouseDialect.Instance.CreateParameter(new QueryParameter("@p0", value)), Throws.TypeOf<NotSupportedException>());

    [Test]
    public void CreateParameter_BinaryOrTimeSpan_Throws()
    {
        Assert.That(() => ClickHouseDialect.Instance.CreateParameter(new QueryParameter("@p0", new byte[] { 1 })), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => ClickHouseDialect.Instance.CreateParameter(new QueryParameter("@p0", TimeSpan.FromHours(1))), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void CreateCommand_NullableEqualityAndNegation_PreserveCSharpSemantics()
    {
        using var connection = new ClickHouseConnection();
        using var equality = SelectExecutor.CreateSelectCommand(connection, TableMapping<Row>.Create(), ClickHouseDialect.Instance,
            row => row.Score == row.OtherScore);
        using var negation = SelectExecutor.CreateSelectCommand(connection, TableMapping<Row>.Create(), ClickHouseDialect.Instance,
            row => !(row.Score > 10));
        Assert.That(equality.CommandText, Does.EndWith(
            "WHERE ((`score` IS NOT NULL AND `other_score` IS NOT NULL AND `score` = `other_score`) OR (`score` IS NULL AND `other_score` IS NULL))"));
        Assert.That(negation.CommandText, Does.EndWith("WHERE (NOT (`score` IS NOT NULL AND `score` > @p0))"));
    }

    [Table("analytics", "users")]
    public class Row
    {
        [Column("user_id")] public int Id { get; set; }
        [Column("is_active")] public bool Active { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("other_score")] public int? OtherScore { get; set; }
        public string? Unmapped { get; set; }
    }
    [Table("analytics", "case_rows")]
    public class CaseRow
    {
        [Column("id")] public int Id { get; set; }
        [Column("ID")] public int OtherId { get; set; }
    }
}
