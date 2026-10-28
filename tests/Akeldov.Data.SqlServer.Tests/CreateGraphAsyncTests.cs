using System.Data;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.SqlServer.Tests;

public partial class CreateGraphTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task CreateAsync_InsertsNestedAndSharedReferencesAndCommits(bool initiallyOpen)
    {
        using var connection = new FakeConnection();
        using var cancellation = new CancellationTokenSource();
        if (initiallyOpen) connection.Open();
        var customer = new Customer { Id = 12, Region = new Region { Id = 7 } };
        var order = new Order { Id = 34, Buyer = customer, Recipient = customer };

        await CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance, order, cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(connection.Executed.Select(command => command.Values),
                Is.EqualTo(new object[][] { [7], [12, 7], [34, 12, 12] }));
            Assert.That(customer.RegionId, Is.EqualTo(7));
            Assert.That(order.BuyerId, Is.EqualTo(12));
            Assert.That(order.RecipientId, Is.EqualTo(12));
            Assert.That(connection.Executed.All(command => command.AsyncExecuted && command.Token == cancellation.Token), Is.True);
            Assert.That(connection.OpenToken, Is.EqualTo(initiallyOpen ? CancellationToken.None : cancellation.Token));
            Assert.That(connection.BeginToken, Is.EqualTo(cancellation.Token));
            Assert.That(connection.Transaction!.CommitToken, Is.EqualTo(cancellation.Token));
            Assert.That(connection.Transaction.Committed, Is.True);
            Assert.That(connection.Transaction.RolledBack, Is.False);
            Assert.That(connection.Transaction.AsyncDisposed, Is.True);
            Assert.That(connection.Commands.All(command => command.AsyncDisposed), Is.True);
            Assert.That(connection.ClosedAsync, Is.EqualTo(!initiallyOpen));
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
            Assert.That(connection.Opens, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CreateAsync_NullReference_PreservesForeignKeyAndSkipsTransaction()
    {
        using var connection = new FakeConnection();
        var customer = new Customer { Id = 12, RegionId = 7 };
        await CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance, customer);
        Assert.That(connection.Executed.Single().Values, Is.EqualTo(new object[] { 12, 7 }));
        Assert.That(customer.RegionId, Is.EqualTo(7));
        Assert.That(connection.Transaction, Is.Null);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        Assert.That(connection.Commands.Single().AsyncDisposed, Is.True);
    }

    [Test]
    public void CreateAsync_InsertFailure_RollsBackAndRestoresKeys(
        [Values] bool initiallyOpen, [Values(1, 2, 3)] int failAt)
    {
        using var connection = new FakeConnection { FailAt = failAt };
        if (initiallyOpen) connection.Open();
        var customer = new Customer { Id = 12, RegionId = 90, Region = new Region { Id = 7 } };
        var order = new Order { BuyerId = 91, RecipientId = 92, Buyer = customer, Recipient = customer };
        Assert.That(async () => await CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance, order),
            Throws.InvalidOperationException.With.Message.EqualTo("insert failed"));
        Assert.Multiple(() =>
        {
            Assert.That(connection.Executed, Has.Count.EqualTo(failAt));
            Assert.That(connection.Transaction!.RolledBack, Is.True);
            Assert.That(connection.Transaction.Committed, Is.False);
            Assert.That(connection.Transaction.AsyncDisposed, Is.True);
            Assert.That(connection.Commands.All(command => command.AsyncDisposed), Is.True);
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
            Assert.That(customer.RegionId, Is.EqualTo(90));
            Assert.That(order.BuyerId, Is.EqualTo(91));
            Assert.That(order.RecipientId, Is.EqualTo(92));
        });
    }

    [TestCase("open")]
    [TestCase("begin")]
    [TestCase("commit")]
    public void CreateAsync_LifecycleFailure_RestoresKeysAndReleasesResources(string stage)
    {
        using var connection = new FakeConnection
        {
            FailOpen = stage == "open", FailBegin = stage == "begin", FailCommit = stage == "commit"
        };
        var customer = new Customer { RegionId = 90, Region = new Region { Id = 7 } };
        Assert.That(async () => await CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance, customer),
            Throws.InvalidOperationException.With.Message.EqualTo(stage + " failed"));
        Assert.That(customer.RegionId, Is.EqualTo(90));
        Assert.That(connection.Commands.All(command => command.AsyncDisposed), Is.True);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        if (stage == "commit")
        {
            Assert.That(connection.Transaction!.RolledBack, Is.True);
            Assert.That(connection.Transaction.Committed, Is.False);
            Assert.That(connection.Transaction.AsyncDisposed, Is.True);
        }
        else Assert.That(connection.Executed, Is.Empty);
    }

    [Test]
    public void CreateAsync_RollbackFailure_PreservesInsertException()
    {
        using var connection = new FakeConnection { FailAt = 2, FailRollback = true };
        var customer = new Customer { RegionId = 90, Region = new Region { Id = 7 } };
        Assert.That(async () => await CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance, customer),
            Throws.InvalidOperationException.With.Message.EqualTo("insert failed"));
        Assert.That(connection.Transaction!.AsyncRollback, Is.True);
        Assert.That(connection.Transaction.AsyncDisposed, Is.True);
        Assert.That(connection.Commands.All(command => command.AsyncDisposed), Is.True);
        Assert.That(customer.RegionId, Is.EqualTo(90));
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void CreateAsync_CancellationAfterInsert_RollsBackWithUncanceledToken(
        [Values] bool initiallyOpen, [Values(1, 2, 3)] int cancelAfter)
    {
        using var connection = new FakeConnection();
        using var cancellation = new CancellationTokenSource();
        if (initiallyOpen) connection.Open();
        connection.AfterExecute = () => { if (connection.Executed.Count == cancelAfter) cancellation.Cancel(); };
        var customer = new Customer { RegionId = 90, Region = new Region { Id = 7 } };
        var order = new Order { BuyerId = 91, RecipientId = 92, Buyer = customer, Recipient = customer };

        Assert.That(async () => await CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance, order, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.Multiple(() =>
        {
            Assert.That(connection.Executed, Has.Count.EqualTo(cancelAfter));
            Assert.That(connection.Transaction!.AsyncRollback, Is.True);
            Assert.That(connection.Transaction.RollbackToken, Is.EqualTo(CancellationToken.None));
            Assert.That(connection.Transaction.RolledBack, Is.True);
            Assert.That(connection.Transaction.Committed, Is.False);
            Assert.That(connection.Transaction.AsyncDisposed, Is.True);
            Assert.That(connection.Commands.All(command => command.AsyncDisposed), Is.True);
            Assert.That(customer.RegionId, Is.EqualTo(90));
            Assert.That(order.BuyerId, Is.EqualTo(91));
            Assert.That(order.RecipientId, Is.EqualTo(92));
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        });
    }

    [Test]
    public void CreateAsync_CancellationDuringCommand_RollsBackAndRestoresKeys()
    {
        using var connection = new FakeConnection();
        using var cancellation = new CancellationTokenSource();
        connection.BeforeExecuteAsync = token =>
        {
            cancellation.Cancel();
            return Task.Delay(Timeout.Infinite, token);
        };
        var customer = new Customer { RegionId = 90, Region = new Region { Id = 7 } };
        Assert.That(async () => await CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance, customer, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.Transaction!.RolledBack, Is.True);
        Assert.That(connection.Commands.All(command => command.AsyncDisposed), Is.True);
        Assert.That(customer.RegionId, Is.EqualTo(90));
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public async Task CreateAsync_AwaitsDependencyBeforeExecutingOwner()
    {
        using var connection = new FakeConnection();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.BeforeExecuteAsync = token => pending.Task.WaitAsync(token);
        var creation = CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance,
            new Customer { Region = new Region { Id = 7 } });
        try
        {
            Assert.That(creation.IsCompleted, Is.False);
            Assert.That(connection.Commands.Count(command => command.AsyncExecuted), Is.EqualTo(1));
            Assert.That(connection.Transaction!.Committed, Is.False);
        }
        finally { pending.SetResult(); }
        await creation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(connection.Executed, Has.Count.EqualTo(2));
        Assert.That(connection.Transaction!.Committed, Is.True);
    }

    [Test]
    public void CreateAsync_PreCanceled_DoesNotPrepareGraphOrOpenConnection()
    {
        using var connection = new FakeConnection();
        var customer = new Customer { RegionId = 90, Region = new Region { Id = 7 } };
        Assert.That(async () => await CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance, customer, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.Commands, Is.Empty);
        Assert.That(connection.Opens, Is.Zero);
        Assert.That(customer.RegionId, Is.EqualTo(90));
    }

    [Test]
    public void CreateAsync_LateMappingFailure_DisposesCommandsAndRestoresKeys()
    {
        using var connection = new FakeConnection();
        var row = new InvalidValue { RegionId = 99, Region = new Region { Id = 7 } };
        Assert.That(async () => await CreateGraphExecutor.CreateAsync(connection, SqlServerDialect.Instance, row),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(row.RegionId, Is.EqualTo(99));
        Assert.That(connection.Commands, Has.Count.EqualTo(2));
        Assert.That(connection.Commands.All(command => command.WasDisposed), Is.True);
        Assert.That(connection.Opens, Is.Zero);
    }

    [Test]
    public void CreateAsync_PublicExtension_ValidatesBeforeOpeningConnection()
    {
        using var connection = new SqlConnection();
        SqlConnection missing = null!;
        var cycle = new RecursiveRow();
        cycle.Next = cycle;
        Assert.Multiple(() =>
        {
            Assert.That(async () => await missing.CreateAsync(new Customer()), Throws.ArgumentNullException);
            Assert.That(async () => await connection.CreateAsync<Customer>(null!), Throws.ArgumentNullException);
            Assert.That(async () => await connection.CreateAsync(new MissingForeignKey()),
                Throws.InvalidOperationException.With.Message.Contains("Column and ForeignKey"));
            Assert.That(async () => await connection.CreateAsync(cycle),
                Throws.InvalidOperationException.With.Message.Contains("Cyclic Reference"));
            Assert.That(async () => await connection.CreateAsync(new Customer(), new CancellationToken(true)),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        });
    }
}
