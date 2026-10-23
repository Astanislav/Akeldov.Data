using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.SqlServer.Tests;

[TestFixture]
public class CreateGraphTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Create_InsertsNestedAndSharedReferencesBeforeOwner(bool initiallyOpen)
    {
        using var connection = new FakeConnection();
        if (initiallyOpen) connection.Open();
        var customer = new Customer { Id = 12, Region = new Region { Id = 7 } };
        var order = new Order { Id = 34, Buyer = customer, Recipient = customer };

        SqlServerCreateExecutor.Create(connection, order);

        Assert.Multiple(() =>
        {
            Assert.That(connection.Executed.Select(command => command.CommandText), Is.EqualTo(new[]
            {
                "INSERT INTO [dbo].[regions] ([id]) VALUES (@p0)",
                "INSERT INTO [dbo].[customers] ([id], [region_id]) VALUES (@p0, @p1)",
                "INSERT INTO [dbo].[orders] ([id], [buyer_id], [recipient_id]) VALUES (@p0, @p1, @p2)"
            }));
            Assert.That(connection.Executed[1].Values, Is.EqualTo(new object[] { 12, 7 }));
            Assert.That(connection.Executed[2].Values, Is.EqualTo(new object[] { 34, 12, 12 }));
            Assert.That(customer.RegionId, Is.EqualTo(7));
            Assert.That(order.BuyerId, Is.EqualTo(12));
            Assert.That(order.RecipientId, Is.EqualTo(12));
            Assert.That(connection.Transaction!.Committed, Is.True);
            Assert.That(connection.Transaction.RolledBack, Is.False);
            Assert.That(connection.Transaction.WasDisposed, Is.True);
            Assert.That(connection.Commands.All(command => command.WasDisposed), Is.True);
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
            Assert.That(connection.Opens, Is.EqualTo(1));
        });
    }

    [Test]
    public void Create_NullReference_PreservesExistingForeignKeyAndSkipsTransaction()
    {
        using var connection = new FakeConnection();
        var customer = new Customer { Id = 12, RegionId = 7 };
        SqlServerCreateExecutor.Create(connection, customer);
        Assert.That(connection.Executed, Has.Count.EqualTo(1));
        Assert.That(connection.Executed[0].Values, Is.EqualTo(new object[] { 12, 7 }));
        Assert.That(connection.Transaction, Is.Null);
    }

    [TestCase(false, 1)]
    [TestCase(false, 3)]
    [TestCase(true, 2)]
    public void Create_InsertFailure_RollsBackAndRestoresForeignKeys(bool initiallyOpen, int failAt)
    {
        using var connection = new FakeConnection { FailAt = failAt };
        if (initiallyOpen) connection.Open();
        var customer = new Customer { Id = 12, RegionId = 90, Region = new Region { Id = 7 } };
        var order = new Order { BuyerId = 91, RecipientId = 92, Buyer = customer, Recipient = customer };
        Assert.That(() => SqlServerCreateExecutor.Create(connection, order),
            Throws.InvalidOperationException.With.Message.EqualTo("insert failed"));
        Assert.Multiple(() =>
        {
            Assert.That(connection.Executed, Has.Count.EqualTo(failAt));
            Assert.That(connection.Transaction!.RolledBack, Is.True);
            Assert.That(connection.Transaction.Committed, Is.False);
            Assert.That(connection.Transaction.WasDisposed, Is.True);
            Assert.That(connection.Commands.All(command => command.WasDisposed), Is.True);
            Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
            Assert.That(customer.RegionId, Is.EqualTo(90));
            Assert.That(order.BuyerId, Is.EqualTo(91));
            Assert.That(order.RecipientId, Is.EqualTo(92));
        });
    }

    [Test]
    public void Create_Cycle_ThrowsBeforeOpeningConnection()
    {
        using var connection = new FakeConnection();
        var first = new RecursiveRow { Id = 1 };
        var second = new RecursiveRow { Id = 2, Next = first };
        first.Next = second;
        Assert.That(() => SqlServerCreateExecutor.Create(connection, first),
            Throws.InvalidOperationException.With.Message.Contains("Cyclic Reference"));
        Assert.That(connection.Opens, Is.Zero);
        Assert.That(connection.Executed, Is.Empty);
    }

    [Test]
    public void Create_InvalidReferenceMetadata_ThrowsBeforeOpeningConnection()
    {
        using var connection = new FakeConnection();
        Assert.Multiple(() =>
        {
            Assert.That(() => SqlServerCreateExecutor.Create(connection, new MissingForeignKey()),
                Throws.InvalidOperationException.With.Message.Contains("Column and ForeignKey"));
            Assert.That(() => SqlServerCreateExecutor.Create(connection, new MissingKey()),
                Throws.InvalidOperationException.With.Message.Contains("Key and Column"));
            Assert.That(() => SqlServerCreateExecutor.Create(connection, new WrongKeyType()),
                Throws.InvalidOperationException.With.Message.Contains("incompatible foreign key and key types"));
            Assert.That(() => SqlServerCreateExecutor.Create(connection, new UnreadableReference()),
                Throws.InvalidOperationException.With.Message.Contains("public getter"));
            Assert.That(() => SqlServerCreateExecutor.Create(connection, new DuplicateReference()),
                Throws.InvalidOperationException.With.Message.Contains("already assigned"));
            Assert.That(connection.Opens, Is.Zero);
            Assert.That(connection.Executed, Is.Empty);
        });
    }

    [Test]
    public void Create_LateMappingFailure_DisposesPreparedCommandsAndRestoresKeys()
    {
        using var connection = new FakeConnection();
        var row = new InvalidValue { RegionId = 99, Region = new Region { Id = 7 } };
        Assert.That(() => SqlServerCreateExecutor.Create(connection, row), Throws.TypeOf<NotSupportedException>());
        Assert.That(row.RegionId, Is.EqualTo(99));
        Assert.That(connection.Commands, Has.Count.EqualTo(2));
        Assert.That(connection.Commands.All(command => command.WasDisposed), Is.True);
        Assert.That(connection.Opens, Is.Zero);
    }

    [Test]
    public void Create_DistinctInstancesWithSameKey_AreNotSilentlyMerged()
    {
        using var connection = new FakeConnection();
        SqlServerCreateExecutor.Create(connection, new Order
        {
            Buyer = new Customer { Id = 12 }, Recipient = new Customer { Id = 12 }
        });
        Assert.That(connection.Executed, Has.Count.EqualTo(3));
    }

    [Test]
    public void Create_NullReferencedKey_ThrowsBeforeOpeningConnection()
    {
        using var connection = new FakeConnection();
        Assert.That(() => SqlServerCreateExecutor.Create(connection, new NullableReference { Child = new NullableKey() }),
            Throws.InvalidOperationException.With.Message.Contains("null referenced key"));
        Assert.That(connection.Opens, Is.Zero);
        Assert.That(connection.Commands.All(command => command.WasDisposed), Is.True);
    }

    [Table("dbo", "regions")]
    public class Region { [Key, Column("id")] public int Id { get; set; } }

    [Table("dbo", "customers")]
    public class Customer
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("region_id"), ForeignKey(typeof(Region), nameof(Region.Id))] public int? RegionId { get; set; }
        [Reference(nameof(RegionId))] public Region? Region { get; set; }
    }

    [Table("dbo", "orders")]
    public class Order
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("buyer_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))] public int BuyerId { get; set; }
        [Column("recipient_id"), ForeignKey(typeof(Customer), nameof(Customer.Id))] public int RecipientId { get; set; }
        [Reference(nameof(BuyerId))] public Customer? Buyer { get; set; }
        [Reference(nameof(RecipientId))] public Customer? Recipient { get; set; }
    }

    [Table("dbo", "recursive")]
    public class RecursiveRow
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("next_id"), ForeignKey(typeof(RecursiveRow), nameof(Id))] public int NextId { get; set; }
        [Reference(nameof(NextId))] public RecursiveRow? Next { get; set; }
    }

    [Table("dbo", "invalid")]
    public class MissingForeignKey
    {
        [Column("id")] public int Id { get; set; }
        [Reference(nameof(Id))] public Region? Region { get; set; }
    }

    [Table("dbo", "invalid")]
    public class MissingKey
    {
        [Column("id"), ForeignKey(typeof(MissingForeignKey), nameof(MissingForeignKey.Id))] public int Id { get; set; }
        [Reference(nameof(Id))] public MissingForeignKey? Child { get; set; }
    }

    [Table("dbo", "invalid")]
    public class WrongKeyType
    {
        [Column("id"), ForeignKey(typeof(Region), nameof(Region.Id))] public string? Id { get; set; }
        [Reference(nameof(Id))] public Region? Region { get; set; }
    }

    [Table("dbo", "invalid")]
    public class UnreadableReference
    {
        [Column("id"), ForeignKey(typeof(Region), nameof(CreateGraphTests.Region.Id))] public int Id { get; set; }
        [Reference(nameof(Id))] public Region Region { set { } }
    }

    [Table("dbo", "invalid")]
    public class InvalidValue
    {
        [Column("region_id"), ForeignKey(typeof(Region), nameof(Region.Id))] public int RegionId { get; set; }
        [Reference(nameof(RegionId))] public Region? Region { get; set; }
        [Column("unsupported")] public object Unsupported { get; set; } = new();
    }

    [Table("dbo", "invalid")]
    public class DuplicateReference
    {
        [Column("id"), ForeignKey(typeof(Region), nameof(Region.Id))] public int Id { get; set; }
        [Reference(nameof(Id))] public Region? First { get; set; }
        [Reference(nameof(Id))] public Region? Second { get; set; }
    }

    [Table("dbo", "nullable_keys")]
    public class NullableKey { [Key, Column("id")] public int? Id { get; set; } }

    [Table("dbo", "nullable_references")]
    public class NullableReference
    {
        [Column("id"), ForeignKey(typeof(NullableKey), nameof(NullableKey.Id))] public int? Id { get; set; }
        [Reference(nameof(Id))] public NullableKey? Child { get; set; }
    }

    private sealed class FakeConnection : DbConnection
    {
        private ConnectionState state;
        public int FailAt { get; init; }
        public int Opens { get; private set; }
        public List<FakeCommand> Commands { get; } = [];
        public List<FakeCommand> Executed { get; } = [];
        public FakeTransaction? Transaction { get; private set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => state;
        public override void Open() { Opens++; state = ConnectionState.Open; }
        public override void Close() => state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => Transaction = new FakeTransaction(this);
        protected override DbCommand CreateDbCommand()
        {
            var command = new FakeCommand(this);
            Commands.Add(command);
            return command;
        }
    }

    private sealed class FakeTransaction(FakeConnection connection) : DbTransaction
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }
        public bool WasDisposed { get; private set; }
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public override void Commit() => Committed = true;
        public override void Rollback() => RolledBack = true;
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class FakeCommand(FakeConnection connection) : DbCommand
    {
        private readonly SqlCommand parameters = new();
        public bool WasDisposed { get; private set; }
        public object[] Values { get; private set; } = [];
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery()
        {
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Open));
            Assert.That(DbTransaction, Is.SameAs(connection.Transaction));
            Values = Parameters.Cast<DbParameter>().Select(parameter => parameter.Value!).ToArray();
            connection.Executed.Add(this);
            if (connection.Executed.Count == connection.FailAt) throw new InvalidOperationException("insert failed");
            return 1;
        }
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new SqlParameter();
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            if (disposing) parameters.Dispose();
            base.Dispose(disposing);
        }
    }
}
