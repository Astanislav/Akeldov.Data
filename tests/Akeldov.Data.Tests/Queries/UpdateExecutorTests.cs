using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using NUnit.Framework;

namespace Akeldov.Data.Tests;

[TestFixture]
public class UpdateExecutorTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void CreateCommand_UsesAllKeysOnlyInWhereAndParameterizesValues(bool positional)
    {
        using var connection = new FakeConnection();
        var row = new CompositeRow { Id = 7, Tenant = "tenant' OR 1=1--", Name = "O'Brien'); DROP TABLE rows;--", State = Status.Active };
        using var command = UpdateExecutor.CreateCommand(connection, new Dialect { Positional = positional }, row);
        var expected = "UPDATE [odd]]schema].[rows]]table] SET [na]]me] = @p0, [score] = NULL, [state] = @p1 WHERE [id] = @p2 AND [tenant] = @p3";
        if (positional)
            for (var index = 0; index < 4; index++) expected = expected.Replace($"@p{index}", $"${index + 1}");
        Assert.Multiple(() =>
        {
            Assert.That(command.CommandText, Is.EqualTo(expected));
            Assert.That(command.Connection, Is.SameAs(connection));
            Assert.That(command.CommandText, Does.Not.Contain(row.Name));
            Assert.That(command.CommandText, Does.Not.Contain(row.Tenant));
            Assert.That(command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value),
                Is.EqualTo(new object[] { row.Name, (short)1, 7, row.Tenant }));
            Assert.That(command.Parameters.Cast<DbParameter>().Select(parameter => parameter.ParameterName),
                Is.EqualTo(new[] { "@p0", "@p1", "@p2", "@p3" }));
            Assert.That(connection.Opens, Is.Zero);
        });
    }

    [Test]
    public void CreateCommand_AllNullAssignmentsAndDefaultKey_AreSupported()
    {
        using var connection = new FakeConnection();
        using var command = UpdateExecutor.CreateCommand(connection, new Dialect(), new Row());
        Assert.That(command.CommandText, Is.EqualTo("UPDATE [dbo].[rows] SET [value] = NULL WHERE [id] = @p0"));
        Assert.That(command.Parameters.Count, Is.EqualTo(1));
        Assert.That(command.Parameters[0].Value, Is.EqualTo(0));
    }

    [TestCaseSource(nameof(ScalarValues))]
    public void CreateCommand_NormalizesValuesLikeCreate(object value, object expected)
    {
        using var connection = new FakeConnection();
        using var command = UpdateExecutor.CreateCommand(connection, new Dialect(), new Row { Id = 1, Value = value });
        Assert.That(command.Parameters[0].Value, Is.EqualTo(expected));
        Assert.That(command.Parameters[0].Value!.GetType(), Is.EqualTo(expected.GetType()));
    }

    private static IEnumerable<TestCaseData> ScalarValues()
    {
        yield return new TestCaseData(Status.Active, (short)1);
        yield return new TestCaseData('x', "x");
        yield return new TestCaseData((sbyte)-2, (short)-2);
        yield return new TestCaseData(ushort.MaxValue, (int)ushort.MaxValue);
        yield return new TestCaseData(uint.MaxValue, (long)uint.MaxValue);
        yield return new TestCaseData(ulong.MaxValue, (decimal)ulong.MaxValue);
        yield return new TestCaseData(12.3400m, 12.3400m);
        yield return new TestCaseData(Guid.Empty, Guid.Empty);
        yield return new TestCaseData(DateTime.UnixEpoch, DateTime.UnixEpoch);
        yield return new TestCaseData(new byte[] { 1, 2 }, new byte[] { 1, 2 });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Update_InvalidKeysOrMapping_ThrowBeforeOpeningConnection(bool asynchronous)
    {
        using var connection = new FakeConnection();
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Update(connection, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("at least one Key"));
            Assert.That(async () => await Update(connection, new UnmappedKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
            Assert.That(async () => await Update(connection, new PartiallyMappedKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
            Assert.That(async () => await Update(connection, new NullableKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Update(connection, new CompositeRow { Id = 7 }, asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Update(connection, new KeyOnly(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("non-key Column"));
            Assert.That(async () => await Update(connection, new WriteOnly(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("public getter"));
            Assert.That(connection.Opens, Is.Zero);
            Assert.That(connection.Command, Is.Null);
        });
    }

    [Test]
    public void CreateCommand_UsesDialectColumnNameComparer()
    {
        using var connection = new FakeConnection();
        Assert.That(() => UpdateExecutor.CreateCommand(connection,
            new Dialect { ColumnNameComparer = StringComparer.OrdinalIgnoreCase }, new CaseRow()),
            Throws.InvalidOperationException.With.Message.Contains("mapped more than once"));
        Assert.That(connection.Command, Is.Null);
        using var command = UpdateExecutor.CreateCommand(connection, new Dialect(), new CaseRow());
        Assert.That(command.CommandText, Is.EqualTo("UPDATE [dbo].[rows] SET [ID] = @p0 WHERE [id] = @p1"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Update_DoesNotTraverseReferencesOrChangeForeignKeys(bool asynchronous)
    {
        using var connection = new FakeConnection();
        var row = new ReferenceRow { Id = 1, ChildId = 9 };
        row.Child = row;
        await Update(connection, row, asynchronous);
        Assert.That(connection.Command!.CommandText, Is.EqualTo("UPDATE [dbo].[rows] SET [child_id] = @p0 WHERE [id] = @p1"));
        Assert.That(connection.Command.Parameters[0].Value, Is.EqualTo(9));
        Assert.That(row.ChildId, Is.EqualTo(9));
        Assert.That(connection.Command.Executions, Is.EqualTo(1));
    }

    [Test]
    public async Task Update_PreservesConnectionStateAndDisposesCommand(
        [Values] bool asynchronous, [Values] bool initiallyOpen, [Values] bool fail)
    {
        using var connection = new FakeConnection { FailExecute = fail };
        using var cancellation = new CancellationTokenSource();
        if (initiallyOpen) connection.Open();
        if (fail)
            Assert.That(async () => await Update(connection, new Row { Id = 1 }, asynchronous, cancellation.Token),
                Throws.InvalidOperationException.With.Message.EqualTo("update failed"));
        else
            await Update(connection, new Row { Id = 1 }, asynchronous, cancellation.Token);
        Assert.Multiple(() =>
        {
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
            Assert.That(connection.Opens, Is.EqualTo(1));
            Assert.That(connection.Command!.WasDisposed, Is.True);
            Assert.That(connection.Command.Executions, Is.EqualTo(1));
            Assert.That(connection.Command.AsyncExecuted, Is.EqualTo(asynchronous));
            Assert.That(connection.Command.AsyncDisposed, Is.EqualTo(asynchronous));
            Assert.That(connection.ClosedAsync, Is.EqualTo(asynchronous && !initiallyOpen));
            Assert.That(connection.Command.Token, Is.EqualTo(asynchronous ? cancellation.Token : CancellationToken.None));
            if (asynchronous && !initiallyOpen) Assert.That(connection.OpenToken, Is.EqualTo(cancellation.Token));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Update_OpenFailure_DisposesCommand(bool asynchronous)
    {
        using var connection = new FakeConnection { FailOpen = true };
        Assert.That(async () => await Update(connection, new Row { Id = 1 }, asynchronous),
            Throws.InvalidOperationException.With.Message.EqualTo("open failed"));
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        Assert.That(connection.Command!.WasDisposed, Is.True);
        Assert.That(connection.Command.Executions, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Update_InvalidValue_DisposesCommandBeforeOpening(bool asynchronous)
    {
        using var connection = new FakeConnection();
        Assert.That(async () => await Update(connection, new Row { Id = 1, Value = new object() }, asynchronous),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(connection.Command!.WasDisposed, Is.True);
        Assert.That(connection.Opens, Is.Zero);
    }

    [Test]
    public void CreateCommand_DialectParameterFailure_DisposesCommand()
    {
        using var connection = new FakeConnection();
        Assert.That(() => UpdateExecutor.CreateCommand(connection, new Dialect { FailParameter = true }, new Row { Id = 1 }),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(connection.Command!.WasDisposed, Is.True);
        Assert.That(connection.Opens, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Update_NullArguments_Throw(bool asynchronous)
    {
        using var connection = new FakeConnection();
        Assert.That(async () => await Update<Row>(connection, null!, asynchronous), Throws.ArgumentNullException);
        Assert.That(async () => await Update(null!, new Row(), asynchronous), Throws.ArgumentNullException);
        Assert.That(() => UpdateExecutor.Update(connection, null!, new Row()), Throws.ArgumentNullException);
        Assert.That(async () => await UpdateExecutor.UpdateAsync(connection, null!, new Row()), Throws.ArgumentNullException);
        Assert.That(connection.Command, Is.Null);
    }

    [Test]
    public void UpdateAsync_PreCanceled_DoesNotPrepareOrOpenConnection()
    {
        using var connection = new FakeConnection();
        Assert.That(async () => await Update(connection, new Row(), true, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.Command, Is.Null);
        Assert.That(connection.Opens, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UpdateAsync_CancellationDuringExecution_DisposesCommandAndPreservesConnection(bool initiallyOpen)
    {
        using var connection = new FakeConnection();
        using var cancellation = new CancellationTokenSource();
        if (initiallyOpen) connection.Open();
        connection.ExecuteAsync = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<int>(token);
        };
        Assert.That(async () => await Update(connection, new Row(), true, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.Command!.AsyncDisposed, Is.True);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    [Test]
    public async Task UpdateAsync_AwaitsCommandBeforeClosingConnection()
    {
        using var connection = new FakeConnection();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ExecuteAsync = token => pending.Task.WaitAsync(token);
        var update = Update(connection, new Row(), true);
        try
        {
            Assert.That(update.IsCompleted, Is.False);
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Open));
            Assert.That(connection.Command!.WasDisposed, Is.False);
        }
        finally { pending.SetResult(0); }
        await update.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(connection.Command!.AsyncDisposed, Is.True);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    private static Task Update<T>(FakeConnection connection, T row, bool asynchronous, CancellationToken token = default)
        where T : class, new()
    {
        if (asynchronous) return UpdateExecutor.UpdateAsync(connection, new Dialect(), row, token);
        UpdateExecutor.Update(connection, new Dialect(), row);
        return Task.CompletedTask;
    }

    [Table("odd]schema", "rows]table")]
    public class CompositeRow
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("tenant")] public string? Tenant { get; set; }
        [Column("na]me")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("state")] public Status State { get; set; }
        public string Unmapped { get; set; } = "ignored";
    }
    public enum Status : short { Active = 1 }
    [Table("dbo", "rows")]
    public class Row
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("value")] public object? Value { get; set; }
    }
    [Table("dbo", "rows")]
    public class MissingKey { [Column("value")] public int Value { get; set; } }
    [Table("dbo", "rows")]
    public class UnmappedKey
    {
        [Key] public int Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }
    [Table("dbo", "rows")]
    public class PartiallyMappedKey
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key] public int Tenant { get; set; }
        [Column("value")] public int Value { get; set; }
    }
    [Table("dbo", "rows")]
    public class NullableKey
    {
        [Key, Column("id")] public int? Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }
    [Table("dbo", "rows")]
    public class KeyOnly { [Key, Column("id")] public int Id { get; set; } }
    [Table("dbo", "rows")]
    public class WriteOnly
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("value")] public string Value { set { } }
    }
    [Table("dbo", "rows")]
    public class CaseRow
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("ID")] public int Value { get; set; }
    }
    [Table("dbo", "rows")]
    public class ReferenceRow
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("child_id"), ForeignKey(typeof(ReferenceRow), nameof(Id))] public int ChildId { get; set; }
        [Reference(nameof(ChildId))] public ReferenceRow? Child { get; set; }
    }

    private sealed class Dialect : ISqlDialect
    {
        public bool Positional { get; init; }
        public bool FailParameter { get; init; }
        public StringComparer ColumnNameComparer { get; init; } = StringComparer.Ordinal;
        public string QuoteIdentifier(string name) => $"[{name.Replace("]", "]]")}]";
        public string GetParameterPlaceholder(int index) => Positional ? $"${index + 1}" : $"@p{index}";
        public string FormatBooleanCondition(string operand) => operand;
        public DbParameter CreateParameter(QueryParameter parameter) => FailParameter
            ? throw new NotSupportedException("parameter not supported")
            : new Parameter { ParameterName = parameter.ParameterName, Value = parameter.Value };
    }

    private sealed class FakeConnection : DbConnection
    {
        private ConnectionState state;
        public bool FailOpen { get; init; }
        public bool FailExecute { get; init; }
        public Func<CancellationToken, Task<int>>? ExecuteAsync { get; set; }
        public int Opens { get; private set; }
        public bool ClosedAsync { get; private set; }
        public CancellationToken OpenToken { get; private set; }
        public FakeCommand? Command { get; private set; }
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => state;
        public override void Open()
        {
            Opens++;
            if (FailOpen) throw new InvalidOperationException("open failed");
            state = ConnectionState.Open;
        }
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            OpenToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            Open();
            return Task.CompletedTask;
        }
        public override void Close() => state = ConnectionState.Closed;
        public override Task CloseAsync() { ClosedAsync = true; Close(); return Task.CompletedTask; }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => Command = new FakeCommand(this);
    }

    private sealed class FakeCommand(FakeConnection connection) : DbCommand
    {
        public bool WasDisposed { get; private set; }
        public bool AsyncDisposed { get; private set; }
        public bool AsyncExecuted { get; private set; }
        public int Executions { get; private set; }
        public CancellationToken Token { get; private set; }
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection { get; } = new Parameters();
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery()
        {
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Open));
            Executions++;
            if (connection.FailExecute) throw new InvalidOperationException("update failed");
            return 1;
        }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            AsyncExecuted = true;
            Token = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return connection.ExecuteAsync?.Invoke(cancellationToken) ?? Task.FromResult(ExecuteNonQuery());
        }
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new Parameter();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { AsyncDisposed = true; Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Parameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = "";
        [AllowNull] public override string SourceColumn { get; set; } = "";
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class Parameters : DbParameterCollection
    {
        private readonly List<DbParameter> items = [];
        public override int Count => items.Count;
        public override object SyncRoot => ((ICollection)items).SyncRoot;
        public override int Add(object value) { items.Add((DbParameter)value); return items.Count - 1; }
        public override void AddRange(Array values) { foreach (var value in values) Add(value!); }
        public override void Clear() => items.Clear();
        public override bool Contains(object value) => items.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => items.GetEnumerator();
        public override int IndexOf(object value) => items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => items.FindIndex(item => item.ParameterName == parameterName);
        public override void Insert(int index, object value) => items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => items[index];
        protected override DbParameter GetParameter(string parameterName) => items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => SetParameter(IndexOf(parameterName), value);
    }
}
