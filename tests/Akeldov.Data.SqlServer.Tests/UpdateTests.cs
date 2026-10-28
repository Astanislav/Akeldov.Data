using System.Data;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.SqlServer.Tests;

[TestFixture]
public class UpdateTests
{
    [Test]
    public void CreateCommand_UsesSqlServerParametersAndCompositeKeys()
    {
        using var connection = new SqlConnection();
        var row = new Row
        {
            Id = 12, TenantId = Guid.NewGuid(), Name = "O'Brien'); DROP TABLE rows;--",
            State = State.Active, Created = DateTime.UnixEpoch.AddTicks(1234567)
        };
        using var command = UpdateExecutor.CreateCommand(connection, SqlServerDialect.Instance, row);
        Assert.Multiple(() =>
        {
            Assert.That(command, Is.TypeOf<SqlCommand>());
            Assert.That(command.Connection, Is.SameAs(connection));
            Assert.That(command.CommandText, Is.EqualTo("UPDATE [odd]]schema].[rows]]table] SET [na]]me] = @p0, [score] = NULL, [state] = @p1, [created] = @p2 WHERE [id] = @p3 AND [tenant_id] = @p4"));
            Assert.That(command.CommandText, Does.Not.Contain(row.Name));
            Assert.That(command.Parameters.Cast<SqlParameter>().Select(parameter => parameter.Value),
                Is.EqualTo(new object[] { row.Name, (short)1, row.Created, 12, row.TenantId }));
            Assert.That(command.Parameters.Cast<SqlParameter>().Select(parameter => parameter.SqlDbType),
                Is.EqualTo(new[] { SqlDbType.NVarChar, SqlDbType.SmallInt, SqlDbType.DateTime2, SqlDbType.Int, SqlDbType.UniqueIdentifier }));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Update_InvalidArgumentsOrMapping_DoNotOpenConnection(bool asynchronous)
    {
        using var connection = new SqlConnection();
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Update<Row>(connection, null!, asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Update(null!, new Row(), asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Update(connection, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("at least one Key"));
            Assert.That(async () => await Update(connection, new UnmappedKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
            Assert.That(async () => await Update(connection, new NullKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Update(connection, new KeyOnly(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("non-key Column"));
            Assert.That(async () => await Update(connection, new CaseCollision(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("mapped more than once"));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void UpdateAsync_PreCanceled_DoesNotOpenConnection()
    {
        using var connection = new SqlConnection();
        Assert.That(async () => await connection.UpdateAsync(new Row(), new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    private static Task Update<T>(SqlConnection connection, T row, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return connection.UpdateAsync(row);
        connection.Update(row);
        return Task.CompletedTask;
    }

    [Table("odd]schema", "rows]table")]
    public class Row
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("tenant_id")] public Guid TenantId { get; set; }
        [Column("na]me")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("state")] public State State { get; set; }
        [Column("created")] public DateTime Created { get; set; }
    }
    public enum State : short { Active = 1 }
    [Table("dbo", "rows")]
    public class MissingKey { [Column("value")] public int Value { get; set; } }
    [Table("dbo", "rows")]
    public class UnmappedKey
    {
        [Key] public int Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }
    [Table("dbo", "rows")]
    public class NullKey
    {
        [Key, Column("id")] public int? Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }
    [Table("dbo", "rows")]
    public class KeyOnly { [Key, Column("id")] public int Id { get; set; } }
    [Table("dbo", "rows")]
    public class CaseCollision
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("ID")] public int Value { get; set; }
    }
}
