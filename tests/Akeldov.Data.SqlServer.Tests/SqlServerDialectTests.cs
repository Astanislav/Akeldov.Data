using System.Data;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.SqlServer.Tests;

[TestFixture]
public class SqlServerDialectTests
{
    [Test]
    public void CreateSelectSql_ColumnNamesDifferingOnlyInCase_RejectsDuplicateMapping()
    {
        Assert.That(() => TableMapping<CaseRow>.Create().CreateSelectSql(SqlServerDialect.Instance),
            Throws.InvalidOperationException.With.Message.Contains("mapped more than once"));
    }

    [Table("dbo", "users")]
    public class CaseRow
    {
        [Column("id")]
        public int Id { get; set; }
        [Column("ID")]
        public int OtherId { get; set; }
    }

    [Test]
    public void CreateSelectSql_QuotesSchemaTableAndColumns()
    {
        Assert.That(TableMapping<Row>.Create().CreateSelectSql(SqlServerDialect.Instance),
            Is.EqualTo("SELECT [select], [na]]me] FROM [custom]]schema].[users]]table]"));
    }

    [Test]
    public void CreateParameter_DateTime_PreservesDateTime2RangeAndPrecision()
    {
        var value = new DateTime(1, 1, 1).AddTicks(1);
        var parameter = (SqlParameter)SqlServerDialect.Instance.CreateParameter(new QueryParameter("@p0", value));
        Assert.That(parameter.SqlDbType, Is.EqualTo(SqlDbType.DateTime2));
        Assert.That(parameter.Value, Is.EqualTo(value));
    }

    [Table("custom]schema", "users]table")]
    public class Row
    {
        [Column("select")]
        public int Id { get; set; }
        [Column("na]me")]
        public string? Name { get; set; }
        public string? Unmapped { get; set; }
    }
}
