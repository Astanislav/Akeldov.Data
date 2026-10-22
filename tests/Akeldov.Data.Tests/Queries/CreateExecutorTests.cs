using System.Data;
using System.Data.Common;
using NUnit.Framework;

namespace Akeldov.Data.Tests;

[TestFixture]
public class CreateExecutorTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void Create_PreservesConnectionAndDisposesCommand(bool initiallyOpen, bool fail)
    {
        using var connection = new FakeConnection { Fail = fail };
        if (initiallyOpen) connection.Open();
        if (fail)
            Assert.That(() => CreateExecutor.Create(connection, new Dialect(), new Row()), Throws.InvalidOperationException);
        else
            CreateExecutor.Create(connection, new Dialect(), new Row());
        Assert.Multiple(() =>
        {
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
            Assert.That(connection.Command!.WasDisposed, Is.True);
            Assert.That(connection.Command.Executions, Is.EqualTo(1));
        });
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task CreateAsync_PreservesConnectionAndDisposesCommand(bool initiallyOpen, bool fail)
    {
        using var connection = new FakeConnection { Fail = fail };
        using var cancellation = new CancellationTokenSource();
        if (initiallyOpen) connection.Open();
        if (fail)
            Assert.That(async () => await CreateExecutor.CreateAsync(connection, new Dialect(), new Row(), cancellation.Token), Throws.InvalidOperationException);
        else
            await CreateExecutor.CreateAsync(connection, new Dialect(), new Row(), cancellation.Token);
        Assert.Multiple(() =>
        {
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
            Assert.That(connection.Command!.WasDisposed, Is.True);
            Assert.That(connection.Command.Token, Is.EqualTo(cancellation.Token));
        });
    }

    [Test]
    public void CreateAsync_CanceledToken_DoesNotCreateCommandOrOpenConnection()
    {
        using var connection = new FakeConnection();
        Assert.That(async () => await CreateExecutor.CreateAsync(connection, new Dialect(), new Row(), new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.Command, Is.Null);
        Assert.That(connection.Opens, Is.Zero);
    }

    [Test]
    public void Create_RejectsNullArgumentsAndUnreadablePropertiesBeforeOpening()
    {
        using var connection = new FakeConnection();
        Assert.Multiple(() =>
        {
            Assert.That(() => CreateExecutor.Create<Row>(null!, new Dialect(), new Row()), Throws.ArgumentNullException);
            Assert.That(() => CreateExecutor.Create<Row>(connection, new Dialect(), null!), Throws.ArgumentNullException);
            Assert.That(() => CreateExecutor.Create(connection, new Dialect(), new WriteOnlyRow()),
                Throws.InvalidOperationException.With.Message.Contains("public getter"));
            Assert.That(connection.Opens, Is.Zero);
        });
    }

    [Test]
    public void Create_ParameterFailure_DisposesCommandWithoutOpeningConnection()
    {
        using var connection = new FakeConnection();
        Assert.That(() => CreateExecutor.Create(connection, new Dialect(), new Row { Name = "value" }), Throws.TypeOf<NotSupportedException>());
        Assert.That(connection.Command!.WasDisposed, Is.True);
        Assert.That(connection.Opens, Is.Zero);
    }

    [Table("dbo", "rows")]
    public class Row { [Column("name")] public string? Name { get; set; } }
    [Table("dbo", "rows")]
    public class WriteOnlyRow { [Column("name")] public string Name { set { } } }

    private sealed class Dialect : ISqlDialect
    {
        public StringComparer ColumnNameComparer => StringComparer.Ordinal;
        public string QuoteIdentifier(string name) => $"[{name}]";
        public string GetParameterPlaceholder(int index) => $"@p{index}";
        public string FormatBooleanCondition(string operand) => operand;
        public DbParameter CreateParameter(QueryParameter parameter) => throw new NotSupportedException();
    }

    private sealed class FakeConnection : DbConnection
    {
        private ConnectionState state;
        public bool Fail { get; init; }
        public int Opens { get; private set; }
        public FakeCommand? Command { get; private set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => state;
        public override void Open() { Opens++; state = ConnectionState.Open; }
        public override void Close() => state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => Command = new FakeCommand(this);
    }

    private sealed class FakeCommand(FakeConnection connection) : DbCommand
    {
        public bool WasDisposed { get; private set; }
        public int Executions { get; private set; }
        public CancellationToken Token { get; private set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery()
        {
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Open));
            Executions++;
            if (connection.Fail) throw new InvalidOperationException("write failed");
            return 1;
        }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            return Task.FromResult(ExecuteNonQuery());
        }
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
