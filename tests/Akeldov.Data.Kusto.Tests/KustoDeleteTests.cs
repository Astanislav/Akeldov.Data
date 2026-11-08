using System.Data;
using System.Reflection;
using Kusto.Data.Common;
using Kusto.Language;
using Kusto.Language.Symbols;
using NUnit.Framework;

namespace Akeldov.Data.Kusto.Tests;

[TestFixture]
public class KustoDeleteTests
{
    [Test]
    public void CreateDeleteCommand_UsesAllKeysAndEscapesIdentifiersAndValues()
    {
        var entity = new Row { Id = 5, Tenant = "quote'\\\n.delete table other records <|" };
        var (database, command) = KustoAdminProviderExtensions.CreateDeleteCommand(entity);
        Assert.That(database, Is.EqualTo("analytics"));
        Assert.That(command, Is.EqualTo("""
            .delete table ['odd\'table'] records <|
            ['odd\'table']
                | where ['id'] == 5 and ['tenant\'id'] == 'quote\'\\\n.delete table other records <|'
            """));
        var table = new TableSymbol("odd'table",
            new ColumnSymbol("id", ScalarTypes.Int), new ColumnSymbol("tenant'id", ScalarTypes.String));
        var globals = GlobalState.Default.WithDatabase(new DatabaseSymbol("analytics", table));
        Assert.That(KustoCode.ParseAndAnalyze(command, globals).GetDiagnostics(), Is.Empty, command);
    }

    [Test]
    public void CreateDeleteCommand_UsesTypedKeyLiterals()
    {
        CheckKey(Guid.Empty, "guid(00000000-0000-0000-0000-000000000000)");
        CheckKey(DateTime.UnixEpoch, "datetime(1970-01-01T00:00:00.0000000Z)");
        CheckKey(TimeSpan.FromMinutes(1), "timespan(00:01:00)");
        CheckKey(true, "true");
        CheckKey(0, "0");
        CheckKey("", "''");
        CheckKey(Status.Active, "1");
    }

    private static void CheckKey<T>(T value, string literal)
    {
        var (_, command) = KustoAdminProviderExtensions.CreateDeleteCommand(new KeyOnly<T> { Id = value });
        Assert.That(command, Is.EqualTo($".delete table ['rows'] records <|\n['rows']\n    | where ['id'] == {literal}"));
        var table = new TableSymbol("rows", new ColumnSymbol("id", ScalarTypes.GetSymbol(KqlParameter.GetScalarType(typeof(T)))));
        var globals = GlobalState.Default.WithDatabase(new DatabaseSymbol("analytics", table));
        Assert.That(KustoCode.ParseAndAnalyze(command, globals).GetDiagnostics(), Is.Empty, command);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_ExecutesOnceConsumesResultAndPreservesProvider(bool asynchronous)
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        var fake = (FakeAdmin)provider;
        var entity = new Row { Id = 5, Tenant = "tenant" };
        entity.Child = entity;
        await Delete(provider, entity, asynchronous);
        Assert.Multiple(() =>
        {
            Assert.That(fake.Database, Is.EqualTo("analytics"));
            Assert.That(fake.Command, Is.EqualTo(KustoAdminProviderExtensions.CreateDeleteCommand(entity).Command));
            Assert.That(fake.Calls, Is.EqualTo(1));
            Assert.That(fake.Method, Is.EqualTo(asynchronous ? "ExecuteControlCommandAsync" : "ExecuteControlCommand"));
            Assert.That(fake.Properties, Is.Not.Null);
            Assert.That(fake.Result.Reads, Is.EqualTo(2));
            Assert.That(fake.Result.Disposed, Is.True);
            Assert.That(fake.Disposed, Is.False);
            Assert.That(entity.Id, Is.EqualTo(5));
            Assert.That(entity.Child, Is.SameAs(entity));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Delete_InvalidArgumentsOrMapping_DoNotSendCommand(bool asynchronous)
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        var fake = (FakeAdmin)provider;
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Delete<Row>(provider, null!, asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Delete(null!, new Row(), asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Delete(provider, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("at least one Key"));
            Assert.That(async () => await Delete(provider, new UnmappedKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
            Assert.That(async () => await Delete(provider, new Row(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Delete(provider, new KeyOnly<int?>(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Delete(provider, new WriteOnlyKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("public getter"));
            Assert.That(async () => await Delete(provider, new DuplicateColumn(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("mapped more than once"));
            Assert.That(async () => await Delete(provider, new KeyOnly<decimal>(), asynchronous), Throws.TypeOf<NotSupportedException>());
            Assert.That(async () => await Delete(provider, new KeyOnly<ulong> { Id = ulong.MaxValue }, asynchronous), Throws.TypeOf<NotSupportedException>());
            Assert.That(async () => await Delete(provider, new KeyOnly<double> { Id = double.NaN }, asynchronous), Throws.TypeOf<NotSupportedException>());
            Assert.That(fake.Calls, Is.Zero);
        });
    }

    [Test]
    public void DeleteAsync_PreCanceled_DoesNotSendCommand()
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        Assert.That(async () => await provider.DeleteAsync(new KeyOnly<int>(), new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(((FakeAdmin)provider).Calls, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Delete_ReadFailure_DisposesResultAndPreservesProvider(bool asynchronous)
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        var fake = (FakeAdmin)provider;
        fake.Result.FailRead = true;
        Assert.That(async () => await Delete(provider, new KeyOnly<int>(), asynchronous),
            Throws.InvalidOperationException.With.Message.EqualTo("read failed"));
        Assert.That(fake.Result.Disposed, Is.True);
        Assert.That(fake.Disposed, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Delete_CommandFailure_PropagatesAndPreservesProvider(bool asynchronous)
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        var fake = (FakeAdmin)provider;
        fake.FailExecute = true;
        Assert.That(async () => await Delete(provider, new KeyOnly<int>(), asynchronous),
            Throws.InvalidOperationException.With.Message.EqualTo("delete failed"));
        Assert.That(fake.Calls, Is.EqualTo(1));
        Assert.That(fake.Disposed, Is.False);
    }

    [Test]
    public async Task DeleteAsync_AwaitsCommandEvenIfCanceledAfterSending()
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        var fake = (FakeAdmin)provider;
        var pending = new TaskCompletionSource<IDataReader>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Pending = pending.Task;
        using var cancellation = new CancellationTokenSource();
        var delete = provider.DeleteAsync(new KeyOnly<int>(), cancellation.Token);
        try
        {
            cancellation.Cancel();
            Assert.That(delete.IsCompleted, Is.False);
            Assert.That(fake.Result.Disposed, Is.False);
        }
        finally { pending.SetResult(fake.Reader); }
        await delete.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fake.Result.Disposed, Is.True);
        Assert.That(fake.Disposed, Is.False);
    }

    private static Task Delete<T>(ICslAdminProvider provider, T entity, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return provider.DeleteAsync(entity);
        provider.Delete(entity);
        return Task.CompletedTask;
    }

    public class FakeAdmin : DispatchProxy
    {
        public IDataReader Reader { get; } = DispatchProxy.Create<IDataReader, FakeReader>();
        public FakeReader Result => (FakeReader)Reader;
        public string? Database { get; private set; }
        public string? Command { get; private set; }
        public string? Method { get; private set; }
        public ClientRequestProperties? Properties { get; private set; }
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailExecute { get; set; }
        public Task<IDataReader>? Pending { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "Dispose") { Disposed = true; return null; }
            Method = targetMethod.Name;
            if (Method is not ("ExecuteControlCommand" or "ExecuteControlCommandAsync")) throw new NotSupportedException(Method);
            Calls++;
            Database = (string)args![0]!;
            Command = (string)args[1]!;
            Properties = (ClientRequestProperties)args[2]!;
            if (FailExecute) throw new InvalidOperationException("delete failed");
            return Method == "ExecuteControlCommandAsync" ? Pending ?? Task.FromResult(Reader) : Reader;
        }
    }

    public class FakeReader : DispatchProxy
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailRead { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "Dispose") { Disposed = true; return null; }
            if (targetMethod.Name != "Read") throw new NotSupportedException(targetMethod.Name);
            Reads++;
            if (FailRead) throw new InvalidOperationException("read failed");
            return Reads == 1;
        }
    }

    [Table("analytics", "odd'table")]
    public class Row
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("tenant'id")] public string? Tenant { get; set; }
        // Delete must not read or serialize non-key values, even unsupported ones.
        [Column("amount")] public decimal Amount { get => throw new InvalidOperationException(); set { } }
        [Reference(nameof(Id))] public Row? Child { get; set; }
    }
    public enum Status { Active = 1 }
    [Table("analytics", "rows")]
    public class KeyOnly<T> { [Key, Column("id")] public T? Id { get; set; } }
    [Table("analytics", "rows")]
    public class MissingKey { [Column("id")] public int Id { get; set; } }
    [Table("analytics", "rows")]
    public class UnmappedKey
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key] public int Tenant { get; set; }
    }
    [Table("analytics", "rows")]
    public class WriteOnlyKey { [Key, Column("id")] public int Id { set { } } }
    [Table("analytics", "rows")]
    public class DuplicateColumn
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("id")] public int Other { get; set; }
    }
}
