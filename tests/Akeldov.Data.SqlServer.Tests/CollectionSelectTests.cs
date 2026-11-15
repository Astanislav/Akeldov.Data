using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.SqlServer.Tests;

[TestFixture]
public class CollectionSelectTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task Select_LoadsListsAndArraysAndPreservesConnection(bool async, bool initiallyOpen)
    {
        using var connection = new FakeConnection();
        if (initiallyOpen) connection.Open();
        connection.Results.Enqueue(Table("id", 1, 2, 3));
        connection.Results.Enqueue(Children());
        connection.Results.Enqueue(Children());
        var rows = async
            ? await SelectExecutor.SelectAsync<Parent>(connection, SqlServerDialect.Instance, row => row.Id > 0)
            : SelectExecutor.Select<Parent>(connection, SqlServerDialect.Instance, row => row.Id > 0);

        Assert.That(rows[0].Items.Select(item => item.Id), Is.EqualTo(new[] { 10, 11 }));
        Assert.That(rows[1].Items.Select(item => item.Id), Is.EqualTo(new[] { 20 }));
        Assert.That(rows[2].Items, Is.Empty);
        Assert.That(rows[0].ArrayItems.Select(item => item.Id), Is.EqualTo(new[] { 10, 11 }));
        Assert.That(rows[2].ArrayItems, Is.Empty);
        Assert.That(connection.Commands, Has.Count.EqualTo(3));
        Assert.That(connection.Commands[0].CommandText, Does.EndWith("WHERE ([id] > @p0)"));
        Assert.That(connection.Commands[1].CommandText,
            Is.EqualTo("SELECT [id], [parent_id] FROM [dbo].[children] WHERE [parent_id] IN (@p0, @p1, @p2)"));
        Assert.That(connection.Commands[1].Parameters.Cast<SqlParameter>().Select(parameter => parameter.Value), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(connection.Commands.All(command => command.WasDisposed), Is.True);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    [Test]
    public void Select_NoParents_DoesNotQueryChildren()
    {
        using var connection = new FakeConnection();
        connection.Results.Enqueue(Table("id"));
        Assert.That(SelectExecutor.Select<Parent>(connection, SqlServerDialect.Instance), Is.Empty);
        Assert.That(connection.Commands, Has.Count.EqualTo(1));
    }

    [Test]
    public void Select_BatchesKeysAndClosesReadersBeforeNextCommand()
    {
        using var connection = new FakeConnection();
        connection.Results.Enqueue(Table("id", Enumerable.Range(1, 2001).ToArray()));
        for (var i = 0; i < 4; i++) connection.Results.Enqueue(Children(false));
        var rows = SelectExecutor.Select<Parent>(connection, SqlServerDialect.Instance);
        Assert.That(rows, Has.Count.EqualTo(2001));
        Assert.That(connection.Commands.Select(command => command.Parameters.Count), Is.EqualTo(new[] { 0, 2000, 1, 2000, 1 }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Select_ChildFailure_DisposesCommandsAndClosesOwnedConnection(bool initiallyOpen)
    {
        using var connection = new FakeConnection();
        if (initiallyOpen) connection.Open();
        connection.Results.Enqueue(Table("id", 1));
        Assert.That(() => SelectExecutor.Select<Parent>(connection, SqlServerDialect.Instance), Throws.InvalidOperationException);
        Assert.That(connection.Commands.All(command => command.WasDisposed), Is.True);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    [Test]
    public void SelectAsync_CancellationDuringChildQuery_ClosesConnection()
    {
        using var cancellation = new CancellationTokenSource();
        using var connection = new FakeConnection { BeforeChild = cancellation.Cancel };
        connection.Results.Enqueue(Table("id", 1));
        Assert.That(async () => await SelectExecutor.SelectAsync<Parent>(connection, SqlServerDialect.Instance,
            cancellationToken: cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.Commands.All(command => command.WasDisposed), Is.True);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    private static DataTable Table(string column, params int[] values)
    {
        var table = new DataTable();
        table.Columns.Add(column, typeof(int));
        foreach (var value in values) table.Rows.Add(value);
        return table;
    }

    private static DataTable Children(bool populated = true)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("parent_id", typeof(int));
        if (populated)
        {
            table.Rows.Add(20, 2);
            table.Rows.Add(10, 1);
            table.Rows.Add(11, 1);
        }
        return table;
    }

    [Table("dbo", "parents")]
    public class Parent
    {
        [Key, Column("id")] public int Id { get; set; }
        [Reference(nameof(Child.ParentId))] public List<Child> Items { get; set; } = null!;
        [Reference(nameof(Child.ParentId))] public Child[] ArrayItems { get; set; } = null!;
    }

    [Table("dbo", "children")]
    public class Child
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("parent_id"), ForeignKey(typeof(Parent), nameof(Parent.Id))] public int? ParentId { get; set; }
    }

    private sealed class FakeConnection : DbConnection
    {
        private ConnectionState state;
        public Queue<DataTable> Results { get; } = new();
        public List<FakeCommand> Commands { get; } = [];
        public DbDataReader? LastReader { get; set; }
        public Action? BeforeChild { get; init; }
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => state;
        public override void Open() => state = ConnectionState.Open;
        public override void Close() => state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand()
        {
            var command = new FakeCommand(this);
            Commands.Add(command);
            return command;
        }
    }

    private sealed class FakeCommand(FakeConnection connection) : DbCommand
    {
        private readonly SqlCommand parameters = new();
        public bool WasDisposed { get; private set; }
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new SqlParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Open));
            Assert.That(connection.LastReader is null || connection.LastReader.IsClosed, Is.True);
            connection.LastReader = connection.Results.Dequeue().CreateDataReader();
            return connection.LastReader;
        }
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken token)
        {
            if (connection.Commands.Count > 1) connection.BeforeChild?.Invoke();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(ExecuteDbDataReader(behavior));
        }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            if (disposing) parameters.Dispose();
            base.Dispose(disposing);
        }
    }
}
