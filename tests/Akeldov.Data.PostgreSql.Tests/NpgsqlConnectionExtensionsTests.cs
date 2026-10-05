using System.Data;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace Akeldov.Data.PostgreSql.Tests;

[TestFixture]
public class NpgsqlConnectionExtensionsTests
{
    [Test]
    public void CreateSelectSql_ColumnNamesDifferingOnlyInCase_PreservesBothNames()
    {
        Assert.That(TableMapping<CaseRow>.Create().CreateSelectSql(PostgreSqlDialect.Instance),
            Is.EqualTo("SELECT \"id\", \"ID\" FROM \"public\".\"users\""));
    }

    [Table("public", "users")]
    public class CaseRow
    {
        [Column("id")]
        public int Id { get; set; }
        [Column("ID")]
        public int OtherId { get; set; }
    }

    [Test]
    public void Select_NullConnection_Throws()
    {
        NpgsqlConnection connection = null!;
        Assert.That(() => connection.Select<Row>(), Throws.ArgumentNullException);
    }

    [Test]
    public void Select_NullPredicate_ThrowsBeforeOpening()
    {
        using var connection = new NpgsqlConnection();
        Assert.That(() => connection.Select<Row>(null!), Throws.ArgumentNullException);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void Select_UnsupportedPredicate_ThrowsBeforeOpening()
    {
        using var connection = new NpgsqlConnection();
        Assert.That(() => connection.Select<Row>(row => row.Id.ToString() == "1"), Throws.TypeOf<NotSupportedException>());
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void Select_MissingTableAttribute_ThrowsBeforeOpening()
    {
        using var connection = new NpgsqlConnection();
        Assert.That(() => connection.Select<Unmapped>(), Throws.InvalidOperationException.With.Message.Contains("Table attribute"));
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void CreateCommand_UsesQuotedNamesPositionalParametersAndBooleanPredicate()
    {
        var minimumId = 5;
        using var connection = new NpgsqlConnection();
        using var command = SelectExecutor.CreateSelectCommand(
            connection, TableMapping<Row>.Create(), PostgreSqlDialect.Instance,
            row => row.Id > minimumId && !row.Active);

        Assert.Multiple(() =>
        {
            Assert.That(command.CommandText,
                Is.EqualTo("SELECT \"user_id\", \"is_active\" FROM \"public\".\"users\" WHERE ((\"user_id\" > $1) AND (NOT (\"is_active\" IS TRUE)))"));
            Assert.That(command.Parameters, Has.Count.EqualTo(1));
            Assert.That(command.Parameters[0].Value, Is.EqualTo(minimumId));
            Assert.That(((NpgsqlParameter)command.Parameters[0]).NpgsqlDbType, Is.EqualTo(NpgsqlDbType.Integer));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void CreateCommand_BooleanParameter_UsesPostgreSqlBoolean()
    {
        var flag = true;
        using var connection = new NpgsqlConnection();
        using var command = SelectExecutor.CreateSelectCommand(
            connection, TableMapping<Row>.Create(), PostgreSqlDialect.Instance, row => row.Active == flag);

        Assert.That(command.CommandText, Does.EndWith("WHERE (\"is_active\" = $1)"));
        Assert.That(((NpgsqlParameter)command.Parameters[0]).NpgsqlDbType, Is.EqualTo(NpgsqlDbType.Boolean));
    }

    [TestCase(DateTimeKind.Unspecified, NpgsqlDbType.Timestamp)]
    [TestCase(DateTimeKind.Utc, NpgsqlDbType.TimestampTz)]
    public void CreateParameter_DateKind_DeterminesTimestampType(DateTimeKind kind, NpgsqlDbType expected)
    {
        var date = new DateTime(2026, 1, 1, 0, 0, 0, kind);
        var parameter = (NpgsqlParameter)PostgreSqlDialect.Instance.CreateParameter(new QueryParameter("$1", date));

        Assert.That(parameter.NpgsqlDbType, Is.EqualTo(expected));
        Assert.That(parameter.Value, Is.EqualTo(date));
    }

    [Test]
    public void QuoteIdentifier_EscapesDoubleQuotes()
    {
        Assert.That(PostgreSqlDialect.Instance.QuoteIdentifier("odd\"name"), Is.EqualTo("\"odd\"\"name\""));
    }

    [Table("public", "users")]
    public class Row
    {
        [Column("user_id")]
        public int Id { get; set; }
        [Column("is_active")]
        public bool Active { get; set; }
    }
    public class Unmapped { }
}
