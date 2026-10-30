using System.Data;
using System.Reflection;
using Kusto.Data.Common;
using Kusto.Language;
using Kusto.Language.Symbols;
using NUnit.Framework;

namespace Akeldov.Data.Kusto.Tests;

[TestFixture]
public class KustoUpdateTests
{
    [Test]
    public void CreateUpdateCommand_UsesCompositeKeysTypedValuesAndEscapedIdentifiers()
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000042");
        var created = new DateTime(2026, 10, 30, 12, 34, 56, DateTimeKind.Utc);
        var entity = new Row
        {
            Id = 5, TenantId = tenantId, Name = "quote'\\\n.update table other <|", Score = null,
            Active = true, State = State.Enabled, Created = created
        };

        var (database, command) = KustoAdminProviderExtensions.CreateUpdateCommand(entity);

        Assert.Multiple(() =>
        {
            Assert.That(database, Is.EqualTo("analytics"));
            Assert.That(command, Is.EqualTo("""
                .update table ['odd\'table'] delete D append A <|
                let D = ['odd\'table']
                    | where ['id'] == 5 and ['tenant_id'] == guid(00000000-0000-0000-0000-000000000042);
                let A = D
                    | extend ['odd\'name'] = 'quote\'\\\n.update table other <|', ['score'] = int(null), ['active'] = true, ['state'] = 1, ['created'] = datetime(2026-10-30T12:34:56.0000000Z);
                """));
            Assert.That(command.Split(".update table"), Has.Length.EqualTo(3));
        });
    }

    [Test]
    public void CreateUpdateCommand_PassesOfficialKustoSyntaxAndTypeAnalysis()
    {
        var (_, command) = KustoAdminProviderExtensions.CreateUpdateCommand(new Row
        {
            Id = 5, TenantId = Guid.NewGuid(), Name = "updated", Score = null,
            Active = true, State = State.Enabled, Created = DateTime.UnixEpoch
        });
        var table = new TableSymbol("odd'table",
            new ColumnSymbol("id", ScalarTypes.Int),
            new ColumnSymbol("tenant_id", ScalarTypes.Guid),
            new ColumnSymbol("odd'name", ScalarTypes.String),
            new ColumnSymbol("score", ScalarTypes.Int),
            new ColumnSymbol("active", ScalarTypes.Bool),
            new ColumnSymbol("state", ScalarTypes.Int),
            new ColumnSymbol("created", ScalarTypes.DateTime));
        var globals = GlobalState.Default.WithDatabase(new DatabaseSymbol("analytics", table));

        var code = KustoCode.ParseAndAnalyze(command, globals);

        Assert.That(code.GetDiagnostics(), Is.Empty, command);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Update_ExecutesCommandAndDisposesResult(bool asynchronous)
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        var fake = (FakeAdmin)provider;
        var entity = new Row { Id = 5, TenantId = Guid.NewGuid() };

        if (asynchronous) await provider.UpdateAsync(entity);
        else provider.Update(entity);

        Assert.Multiple(() =>
        {
            Assert.That(fake.Database, Is.EqualTo("analytics"));
            Assert.That(fake.Command, Does.StartWith(".update table ['odd\\'table']"));
            Assert.That(fake.Command, Does.Contain("['odd\\'name'] = ''"));
            Assert.That(fake.Reader.IsClosed, Is.True);
            Assert.That(fake.Disposed, Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Update_InvalidArgumentsOrMapping_DoNotSendCommand(bool asynchronous)
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        var fake = (FakeAdmin)provider;
        Assert.Multiple(() =>
        {
            Assert.That(async () => await Update<Row>(provider, null!, asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Update<Row>(null!, new Row(), asynchronous), Throws.ArgumentNullException);
            Assert.That(async () => await Update(provider, new MissingKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("at least one Key"));
            Assert.That(async () => await Update(provider, new UnmappedKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("Column attribute"));
            Assert.That(async () => await Update(provider, new NullKey(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("must not be null"));
            Assert.That(async () => await Update(provider, new KeyOnly(), asynchronous),
                Throws.InvalidOperationException.With.Message.Contains("non-key Column"));
            Assert.That(async () => await Update(provider, new Unsupported(), asynchronous),
                Throws.TypeOf<NotSupportedException>());
            Assert.That(fake.Command, Is.Null);
        });
        fake.Reader.Dispose();
    }

    [Test]
    public void UpdateAsync_PreCanceled_DoesNotSendCommand()
    {
        var provider = DispatchProxy.Create<ICslAdminProvider, FakeAdmin>();
        var fake = (FakeAdmin)provider;
        Assert.That(async () => await provider.UpdateAsync(new Row(), new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.Command, Is.Null);
        fake.Reader.Dispose();
    }

    private static Task Update<T>(ICslAdminProvider provider, T entity, bool asynchronous) where T : class, new()
    {
        if (asynchronous) return provider.UpdateAsync(entity);
        provider.Update(entity);
        return Task.CompletedTask;
    }

    public class FakeAdmin : DispatchProxy
    {
        public IDataReader Reader { get; } = new DataTable().CreateDataReader();
        public string? Database { get; private set; }
        public string? Command { get; private set; }
        public bool Disposed { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "Dispose") { Disposed = true; return null; }
            Database = (string)args![0]!;
            Command = (string)args[1]!;
            return targetMethod.Name == "ExecuteControlCommandAsync" ? Task.FromResult(Reader) : Reader;
        }
    }

    [Table("analytics", "odd'table")]
    public class Row
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("tenant_id")] public Guid TenantId { get; set; }
        [Column("odd'name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("active")] public bool Active { get; set; }
        [Column("state")] public State State { get; set; }
        [Column("created")] public DateTime Created { get; set; }
    }

    public enum State { Disabled, Enabled }
    [Table("analytics", "rows")]
    public class MissingKey { [Column("value")] public int Value { get; set; } }
    [Table("analytics", "rows")]
    public class UnmappedKey
    {
        [Key] public int Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }
    [Table("analytics", "rows")]
    public class NullKey
    {
        [Key, Column("id")] public int? Id { get; set; }
        [Column("value")] public int Value { get; set; }
    }
    [Table("analytics", "rows")]
    public class KeyOnly { [Key, Column("id")] public int Id { get; set; } }
    [Table("analytics", "rows")]
    public class Unsupported
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("amount")] public decimal Amount { get; set; }
    }
}
