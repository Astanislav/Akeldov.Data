using System.Data;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace Akeldov.Data.PostgreSql.Tests;

[TestFixture]
public class DeleteTests
{
    [Test]
    public void CreateCommand_UsesTypedPositionalParametersAndAllKeys()
    {
        using var connection = new NpgsqlConnection();
        var row = new Row { Id = 12, Tenant = "O'Brien'); DROP TABLE rows;--", Value = "ignored" };
        using var command = DeleteExecutor.CreateCommand(connection, PostgreSqlDialect.Instance, row);
        Assert.Multiple(() =>
        {
            Assert.That(command, Is.TypeOf<NpgsqlCommand>());
            Assert.That(command.Connection, Is.SameAs(connection));
            Assert.That(command.CommandText,
                Is.EqualTo("""DELETE FROM "odd""schema"."rows""table" WHERE "id" = $1 AND "tenant""id" = $2"""));
            Assert.That(command.CommandText, Does.Not.Contain(row.Tenant));
            Assert.That(command.Parameters.Cast<NpgsqlParameter>().Select(parameter => parameter.Value),
                Is.EqualTo(new object[] { row.Id, row.Tenant }));
            Assert.That(command.Parameters.Cast<NpgsqlParameter>().Select(parameter => parameter.NpgsqlDbType),
                Is.EqualTo(new[] { NpgsqlDbType.Integer, NpgsqlDbType.Text }));
            Assert.That(command.Parameters.Cast<NpgsqlParameter>().All(parameter => parameter.ParameterName == ""), Is.True);
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void CreateCommand_KeyOnlyModel_IsSupported()
    {
        using var connection = new NpgsqlConnection();
        using var command = DeleteExecutor.CreateCommand(connection, PostgreSqlDialect.Instance, new KeyOnly());
        Assert.That(command.CommandText, Is.EqualTo("""DELETE FROM "public"."rows" WHERE "id" = $1"""));
        Assert.That(command.Parameters[0].Value, Is.EqualTo(0));
    }

    [Test]
    public void CreateCommand_PreservesCaseDistinctKeys()
    {
        using var connection = new NpgsqlConnection();
        using var command = DeleteExecutor.CreateCommand(connection, PostgreSqlDialect.Instance,
            new CaseRow { Id = 1, AlternateId = 2 });
        Assert.That(command.CommandText,
            Is.EqualTo("""DELETE FROM "public"."CaseRows" WHERE "id" = $1 AND "ID" = $2"""));
        Assert.That(command.Parameters.Cast<NpgsqlParameter>().Select(parameter => parameter.Value),
            Is.EqualTo(new[] { 1, 2 }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Delete_InvalidArgumentsOrMapping_DoNotOpenConnection(bool asynchronous)
    {
        using var connection = new NpgsqlConnection();
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Delete<Row>(connection, null!, asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Delete(null!, new Row(), asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Delete(connection, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("at least one Key"));
            Assert.That(async () => await Delete(connection, new UnmappedKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
            Assert.That(async () => await Delete(connection, new Row(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Delete(connection, new DuplicateColumn(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("mapped more than once"));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void DeleteAsync_PreCanceled_DoesNotOpenConnection()
    {
        using var connection = new NpgsqlConnection();
        Assert.That(async () => await connection.DeleteAsync(new KeyOnly(), new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    private static Task Delete<T>(NpgsqlConnection connection, T row, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return connection.DeleteAsync(row);
        connection.Delete(row);
        return Task.CompletedTask;
    }

    [Table("odd\"schema", "rows\"table")]
    public class Row
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("tenant\"id")] public string? Tenant { get; set; }
        [Column("value")] public string? Value { get; set; }
    }

    [Table("public", "rows")]
    public class KeyOnly { [Key, Column("id")] public int Id { get; set; } }

    [Table("public", "rows")]
    public class MissingKey { [Column("value")] public int Value { get; set; } }

    [Table("public", "rows")]
    public class UnmappedKey
    {
        [Key] public int Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }

    [Table("public", "rows")]
    public class DuplicateColumn
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("id")] public int Value { get; set; }
    }

    [Table("public", "CaseRows")]
    public class CaseRow
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("ID")] public int AlternateId { get; set; }
    }
}
