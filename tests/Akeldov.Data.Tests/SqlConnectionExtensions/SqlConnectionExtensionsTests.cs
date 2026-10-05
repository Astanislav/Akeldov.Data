using System.Data;
using System.Linq.Expressions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.Tests;

[TestFixture]
public class SqlConnectionExtensionsTests
{
    [Test]
    public void Select_WithPredicate_NullConnection_ThrowsArgumentNullException()
    {
        SqlConnection connection = null!;

        Assert.That(() => connection.Select<User>(user => user.Id > 1), Throws.ArgumentNullException);
    }

    [Test]
    public void Select_NullPredicate_ThrowsBeforeOpeningConnection()
    {
        using var connection = new SqlConnection();
        Expression<Func<User, bool>> predicate = null!;

        Assert.That(() => connection.Select(predicate), Throws.ArgumentNullException);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void Select_UnsupportedPredicate_ThrowsBeforeOpeningConnection()
    {
        using var connection = new SqlConnection();

        Assert.That(() => connection.Select<User>(user => user.Id.ToString() == "1"), Throws.TypeOf<NotSupportedException>());
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void Select_WithPredicate_ModelWithoutTable_ThrowsBeforeOpeningConnection()
    {
        using var connection = new SqlConnection();

        Assert.That(() => connection.Select<UnmappedRow>(row => true),
            Throws.InvalidOperationException.With.Message.Contains("Table attribute"));
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void CreateSelectCommand_AttachesPredicateAndSqlParameters()
    {
        const int minId = 5;
        using var connection = new SqlConnection();
        using var command = SqlConnectionExtensions.CreateSelectCommand(
            connection, TableMapping<User>.Create(), user => user.Id >= minId && user.Id < 20);

        Assert.Multiple(() =>
        {
            Assert.That(command.CommandText,
                Is.EqualTo("SELECT [user_id] FROM [dbo].[users] WHERE (([user_id] >= @p0) AND ([user_id] < @p1))"));
            Assert.That(command.Connection, Is.SameAs(connection));
            Assert.That(command.Parameters, Has.Count.EqualTo(2));
            Assert.That(command.Parameters["@p0"].Value, Is.EqualTo(minId));
            Assert.That(command.Parameters["@p1"].Value, Is.EqualTo(20));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    [Test]
    public void CreateSelectCommand_WithoutPredicate_PreservesUnfilteredQuery()
    {
        using var connection = new SqlConnection();
        using var command = SqlConnectionExtensions.CreateSelectCommand(connection, TableMapping<User>.Create());

        Assert.Multiple(() =>
        {
            Assert.That(command.CommandText, Is.EqualTo("SELECT [user_id] FROM [dbo].[users]"));
            Assert.That(command.Parameters, Is.Empty);
        });
    }

    [Test]
    public void Select_NullConnection_ThrowsArgumentNullException()
    {
        SqlConnection connection = null!;

        Assert.That(() => connection.Select<User>(), Throws.ArgumentNullException);
    }

    [Test]
    public void Select_ModelWithoutTable_ThrowsBeforeOpeningConnection()
    {
        using var connection = new SqlConnection();

        Assert.That(() => connection.Select<UnmappedRow>(),
            Throws.InvalidOperationException.With.Message.Contains("Table attribute"));
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void Select_ModelWithoutColumns_ThrowsBeforeOpeningConnection()
    {
        using var connection = new SqlConnection();

        Assert.That(() => connection.Select<RowWithoutColumns>(),
            Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void Select_FailedOpen_LeavesConnectionClosedAndUsable()
    {
        using var connection = new SqlConnection();

        Assert.That(() => connection.Select<User>(), Throws.InvalidOperationException);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        Assert.DoesNotThrow(() => connection.ConnectionString = "Server=localhost;Database=example;Integrated Security=true;");
    }

    [Table("dbo", "users")]
    public class User
    {
        [Column("user_id")]
        public int Id { get; set; }
    }

    public class UnmappedRow
    {
    }

    [Table("dbo", "users")]
    public class RowWithoutColumns
    {
        public int Id { get; set; }
    }
}
