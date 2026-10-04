using System.Data;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.Tests;

[TestFixture]
public class SqlConnectionExtensionsTests
{
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
