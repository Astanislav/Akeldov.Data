using System.Data;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.SqlServer.Tests;

[TestFixture]
public class DeleteTests
{
    [Test]
    public void CreateCommand_UsesSqlServerParametersAndAllKeys()
    {
        using var connection = new SqlConnection();
        var row = new Row { Id = 12, Tenant = "O'Brien'); DROP TABLE rows;--", Value = "ignored" };
        using var command = DeleteExecutor.CreateCommand(connection, SqlServerDialect.Instance, row);
        Assert.Multiple(() =>
        {
            Assert.That(command, Is.TypeOf<SqlCommand>());
            Assert.That(command.Connection, Is.SameAs(connection));
            Assert.That(command.CommandText,
                Is.EqualTo("DELETE FROM [odd]]schema].[rows]]table] WHERE [id] = @p0 AND [tenant]]id] = @p1"));
            Assert.That(command.CommandText, Does.Not.Contain(row.Tenant));
            Assert.That(command.Parameters.Cast<SqlParameter>().Select(parameter => parameter.Value),
                Is.EqualTo(new object[] { row.Id, row.Tenant }));
            Assert.That(command.Parameters.Cast<SqlParameter>().Select(parameter => parameter.SqlDbType),
                Is.EqualTo(new[] { SqlDbType.Int, SqlDbType.NVarChar }));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void CreateCommand_KeyOnlyModel_IsSupported()
    {
        using var connection = new SqlConnection();
        using var command = DeleteExecutor.CreateCommand(connection, SqlServerDialect.Instance, new KeyOnly());
        Assert.That(command.CommandText, Is.EqualTo("DELETE FROM [dbo].[rows] WHERE [id] = @p0"));
        Assert.That(command.Parameters[0].Value, Is.EqualTo(0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Delete_InvalidArgumentsOrMapping_DoNotOpenConnection(bool asynchronous)
    {
        using var connection = new SqlConnection();
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
            Assert.That(async () => await Delete(connection, new CaseCollision(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("mapped more than once"));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void DeleteAsync_PreCanceled_DoesNotOpenConnection()
    {
        using var connection = new SqlConnection();
        Assert.That(async () => await connection.DeleteAsync(new KeyOnly(), new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    private static Task Delete<T>(SqlConnection connection, T row, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return connection.DeleteAsync(row);
        connection.Delete(row);
        return Task.CompletedTask;
    }

    [Table("odd]schema", "rows]table")]
    public class Row
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("tenant]id")] public string? Tenant { get; set; }
        [Column("value")] public string? Value { get; set; }
    }

    [Table("dbo", "rows")]
    public class KeyOnly { [Key, Column("id")] public int Id { get; set; } }

    [Table("dbo", "rows")]
    public class MissingKey { [Column("value")] public int Value { get; set; } }

    [Table("dbo", "rows")]
    public class UnmappedKey
    {
        [Key] public int Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }

    [Table("dbo", "rows")]
    public class CaseCollision
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("ID")] public int Value { get; set; }
    }
}
