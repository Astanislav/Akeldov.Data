using System.Data;
using Npgsql;
using NUnit.Framework;

namespace Akeldov.Data.PostgreSql.Tests;

[TestFixture]
public class CreateTests
{
    [Test]
    public void CreateCommand_UsesMappedColumnsParametersNullsAndEnumValues()
    {
        using var connection = new NpgsqlConnection();
        const string name = "Robert'); DROP TABLE rows;-- Ольга";
        var date = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        using var command = CreateExecutor.CreateCommand(connection, PostgreSqlDialect.Instance,
            new Row { Id = 12, Name = name, State = State.Active, Created = date });
        Assert.Multiple(() =>
        {
            Assert.That(command.CommandText, Is.EqualTo("INSERT INTO \"odd]schema\".\"rows]table\" (\"id\", \"name\", \"score\", \"state\", \"created\") VALUES ($1, $2, NULL, $3, $4)"));
            Assert.That(command.Connection, Is.SameAs(connection));
            Assert.That(command.Parameters, Has.Count.EqualTo(4));
            Assert.That(command.Parameters[0].Value, Is.EqualTo(12));
            Assert.That(command.Parameters[1].Value, Is.EqualTo(name));
            Assert.That(command.Parameters[2].Value, Is.EqualTo((short)1));
            Assert.That(command.Parameters[3].Value, Is.EqualTo(date));
            Assert.That(((Npgsql.NpgsqlParameter)command.Parameters[3]).NpgsqlDbType, Is.EqualTo(NpgsqlTypes.NpgsqlDbType.TimestampTz));
        });
    }

    [Test]
    public void Create_InvalidArgumentsOrMapping_DoNotOpenConnection()
    {
        using var connection = new NpgsqlConnection();
        NpgsqlConnection missing = null!;
        Assert.Multiple(() =>
        {
            Assert.That(() => missing.Create(new Row()), Throws.ArgumentNullException);
            Assert.That(() => connection.Create<Row>(null!), Throws.ArgumentNullException);
            Assert.That(() => connection.Create(new Unmapped()), Throws.InvalidOperationException);
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Table("odd]schema", "rows]table")]
    public class Row
    {
        [Column("id")] public int Id { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("state")] public State State { get; set; }
        [Column("created")] public DateTime Created { get; set; }
        public string Unmapped { get; set; } = "ignored";
    }
    public enum State : short { Active = 1 }
    public class Unmapped { }
}
