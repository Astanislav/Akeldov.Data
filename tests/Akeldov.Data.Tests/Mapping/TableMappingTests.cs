using System.Data;
using NUnit.Framework;

namespace Akeldov.Data.Tests;

[TestFixture]
public class TableMappingTests
{
    [Test]
    public void Create_PreservesTableAndColumnNames()
    {
        var mapping = TableMapping<EscapedRow>.Create();

        Assert.Multiple(() =>
        {
            Assert.That(mapping.Table.Schema, Is.EqualTo("custom]schema"));
            Assert.That(mapping.Table.Name, Is.EqualTo("users]table"));
            Assert.That(mapping.GetColumnName(typeof(EscapedRow).GetProperty(nameof(EscapedRow.Name))!), Is.EqualTo("na]me"));
        });
    }

    [Test]
    public void Create_ReadOnlyProperty_Throws()
    {
        Assert.That(() => TableMapping<ReadOnlyRow>.Create(),
            Throws.InvalidOperationException.With.Message.Contains("public setter"));
    }

    [Test]
    public void Create_PrivateSetter_Throws()
    {
        Assert.That(() => TableMapping<PrivateSetterRow>.Create(),
            Throws.InvalidOperationException.With.Message.Contains("public setter"));
    }

    [Test]
    public void Create_Indexer_Throws()
    {
        Assert.That(() => TableMapping<IndexerRow>.Create(),
            Throws.InvalidOperationException.With.Message.Contains("indexer"));
    }

    [Test]
    public void Create_DuplicateColumnNames_Throws()
    {
        Assert.That(() => TableMapping<DuplicateColumnsRow>.Create(),
            Throws.InvalidOperationException.With.Message.Contains("mapped more than once"));
    }

    [Test]
    public void Read_MapsMultipleRowsByColumnNameAndHandlesNulls()
    {
        using var table = new DataTable();
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("score", typeof(int));
        table.Columns.Add("user_id", typeof(int));
        table.Rows.Add("Alice", 10, 1);
        table.Rows.Add(DBNull.Value, DBNull.Value, 2);
        using var reader = table.CreateDataReader();

        var rows = TableMapping<User>.Create().Read(reader);

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Id, Is.EqualTo(1));
            Assert.That(rows[0].Name, Is.EqualTo("Alice"));
            Assert.That(rows[0].Score, Is.EqualTo(10));
            Assert.That(rows[1].Id, Is.EqualTo(2));
            Assert.That(rows[1].Name, Is.Null);
            Assert.That(rows[1].Score, Is.Null);
            Assert.That(rows[0].Unmapped, Is.EqualTo("default"));
        });
    }

    [Test]
    public void Read_EmptyResult_ReturnsEmptyList()
    {
        using var table = new DataTable();
        table.Columns.Add("user_id", typeof(int));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("score", typeof(int));
        using var reader = table.CreateDataReader();

        Assert.That(TableMapping<User>.Create().Read(reader), Is.Empty);
    }

    [Test]
    public void Read_NullForNonNullableValueType_Throws()
    {
        using var table = new DataTable();
        table.Columns.Add("value", typeof(int));
        table.Rows.Add(DBNull.Value);
        using var reader = table.CreateDataReader();

        Assert.That(() => TableMapping<ValueRow>.Create().Read(reader),
            Throws.InvalidOperationException.With.Message.Contains("not nullable"));
    }

    [Test]
    public void Read_IncompatibleValue_ThrowsWithColumnName()
    {
        using var table = new DataTable();
        table.Columns.Add("value", typeof(string));
        table.Rows.Add("not a number");
        using var reader = table.CreateDataReader();

        Assert.That(() => TableMapping<ValueRow>.Create().Read(reader),
            Throws.InvalidOperationException.With.Message.Contains("Cannot map column 'value'"));
    }

    [Test]
    public void Read_ConvertsNumericEnumAndPreservesGuidAndBinaryValues()
    {
        var id = Guid.NewGuid();
        var payload = new byte[] { 1, 2, 3 };
        using var table = new DataTable();
        table.Columns.Add("count", typeof(int));
        table.Columns.Add("state", typeof(int));
        table.Columns.Add("id", typeof(Guid));
        table.Columns.Add("payload", typeof(byte[]));
        table.Rows.Add(42, 1, id, payload);
        using var reader = table.CreateDataReader();

        var rows = TableMapping<TypedRow>.Create().Read(reader);

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Count, Is.EqualTo(42L));
            Assert.That(rows[0].State, Is.EqualTo(RowState.Active));
            Assert.That(rows[0].Id, Is.EqualTo(id));
            Assert.That(rows[0].Payload, Is.EqualTo(payload));
        });
    }

    [Table("custom]schema", "users]table")]
    public class EscapedRow
    {
        [Column("select")]
        public int Id { get; set; }

        [Column("na]me")]
        public string? Name { get; set; }

        public string? Unmapped { get; set; }
    }

    [Table("dbo", "users")]
    public class User
    {
        [Column("user_id")]
        public int Id { get; set; }

        [Column("name")]
        public string? Name { get; set; }

        [Column("score")]
        public int? Score { get; set; }

        public string Unmapped { get; set; } = "default";
    }

    [Table("dbo", "values")]
    public class ValueRow
    {
        [Column("value")]
        public int Value { get; set; }
    }

    [Table("dbo", "typed_rows")]
    public class TypedRow
    {
        [Column("count")]
        public long Count { get; set; }

        [Column("state")]
        public RowState State { get; set; }

        [Column("id")]
        public Guid Id { get; set; }

        [Column("payload")]
        public byte[]? Payload { get; set; }
    }

    public enum RowState
    {
        Inactive,
        Active
    }

    [Table("dbo", "users")]
    public class ReadOnlyRow
    {
        [Column("user_id")]
        public int Id => 1;
    }

    [Table("dbo", "users")]
    public class PrivateSetterRow
    {
        [Column("user_id")]
        public int Id { get; private set; }
    }

    [Table("dbo", "users")]
    public class IndexerRow
    {
        [Column("user_id")]
        public int this[int index]
        {
            get => index;
            set { }
        }
    }

    [Table("dbo", "users")]
    public class DuplicateColumnsRow
    {
        [Column("user_id")]
        public int Id { get; set; }

        [Column("user_id")]
        public int OtherId { get; set; }
    }
}
