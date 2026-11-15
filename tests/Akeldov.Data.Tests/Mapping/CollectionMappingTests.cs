using NUnit.Framework;

namespace Akeldov.Data.Tests.Mapping;

[TestFixture]
public class CollectionMappingTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void Attribute_RejectsEmptyForeignKey(string? name)
        => Assert.That(() => new CollectionAttribute(name!), Throws.InstanceOf<ArgumentException>());

    [Test]
    public void Create_ResolvesChildForeignKeyAndParentKeyWithoutReadingCollection()
    {
        var mapping = TableMapping<Order>.Create();
        var property = typeof(Order).GetProperty(nameof(Order.Items))!;
        var relation = mapping.GetCollection(property);

        Assert.That(relation.ElementType, Is.EqualTo(typeof(Item)));
        Assert.That(relation.Table.Name, Is.EqualTo("items"));
        Assert.That(relation.ParentKey.Name, Is.EqualTo(nameof(Order.Id)));
        Assert.That(relation.ParentKeyColumn, Is.EqualTo("order_id"));
        Assert.That(relation.ForeignKey.Name, Is.EqualTo(nameof(Item.OrderId)));
        Assert.That(relation.ForeignKeyColumn, Is.EqualTo("owner_id"));
        Assert.That(mapping.ColumnNames, Is.EqualTo(new[] { "order_id" }));
        Assert.That(mapping.Collections, Has.Count.EqualTo(4));
        Assert.That(() => mapping.GetCollection(typeof(Order).GetProperty(nameof(Order.Unmapped))!),
            Throws.TypeOf<NotSupportedException>());
    }

    [TestCase(typeof(MissingForeignKey), nameof(MissingForeignKey.Items))]
    [TestCase(typeof(WrongParent), nameof(WrongParent.Items))]
    [TestCase(typeof(InvalidNavigation), nameof(InvalidNavigation.Text))]
    [TestCase(typeof(InvalidNavigation), nameof(InvalidNavigation.Scalar))]
    [TestCase(typeof(InvalidNavigation), nameof(InvalidNavigation.Column))]
    [TestCase(typeof(InvalidNavigation), nameof(InvalidNavigation.Reference))]
    [TestCase(typeof(InvalidNavigation), nameof(InvalidNavigation.UnmappedChildren))]
    [TestCase(typeof(IncompatibleParent), nameof(IncompatibleParent.Items))]
    [TestCase(typeof(CompositeParent), nameof(CompositeParent.Items))]
    public void Create_InvalidRelationship_Throws(Type parent, string name)
    {
        var property = parent.GetProperty(name)!;
        var attribute = (CollectionAttribute)Attribute.GetCustomAttribute(property, typeof(CollectionAttribute))!;
        Assert.That(() => CollectionMapping.Create(parent, property, attribute),
            Throws.InvalidOperationException.With.Message.Contains("Collection"));
    }

    [Test]
    public void TableMapping_ValidatesCollectionsDuringCreation()
        => Assert.That(() => TableMapping<WrongParent>.Create(), Throws.InvalidOperationException);

    [Test]
    public void Create_SelfReference_DoesNotRecursivelyBuildMappings()
    {
        var relation = TableMapping<Node>.Create().Collections.Single();
        Assert.That(relation.ElementType, Is.EqualTo(typeof(Node)));
        Assert.That(relation.ForeignKey.Name, Is.EqualTo(nameof(Node.ParentId)));
    }

    [Table("dbo", "orders")]
    public class Order
    {
        [Key, Column("order_id")] public int Id { get; set; }
        [Collection(nameof(Item.OrderId))] public List<Item> Items => throw new InvalidOperationException();
        [Collection(nameof(Item.OrderId))] public Item[] ArrayItems { get; set; } = [];
        [Collection(nameof(Item.OrderId))] public ICollection<Item> CollectionItems { get; set; } = new List<Item>();
        [Collection(nameof(Item.OrderId))] public IEnumerable<Item> EnumerableItems { get; set; } = [];
        public List<Item> Unmapped { get; set; } = [];
    }

    [Table("dbo", "items")]
    public class Item
    {
        [Column("owner_id"), ForeignKey(typeof(Order), nameof(Order.Id))] public int? OrderId { get; set; }
    }

    public class MissingForeignKey
    {
        [Collection("Missing")] public List<Item> Items { get; set; } = [];
    }

    [Table("dbo", "wrong")]
    public class WrongParent
    {
        [Key, Column("id")] public int Id { get; set; }
        [Collection(nameof(Item.OrderId))] public List<Item> Items { get; set; } = [];
    }

    public class InvalidNavigation
    {
        [Collection("Id")] public string Text { get; set; } = "";
        [Collection("Id")] public Item Scalar { get; set; } = new();
        [Collection("Id"), Column("items")] public List<Item> Column { get; set; } = [];
        [Collection("Id"), Reference("Id")] public List<Item> Reference { get; set; } = [];
        [Collection("Id")] public List<MissingForeignKey> UnmappedChildren { get; set; } = [];
    }

    public class IncompatibleParent
    {
        [Key, Column("id")] public int Id { get; set; }
        [Collection(nameof(IncompatibleItem.ParentId))] public List<IncompatibleItem> Items { get; set; } = [];
    }

    [Table("dbo", "items")]
    public class IncompatibleItem
    {
        [Column("parent_id"), ForeignKey(typeof(IncompatibleParent), nameof(IncompatibleParent.Id))]
        public string ParentId { get; set; } = "";
    }

    public class CompositeParent
    {
        [Key, Column("id")] public int Id { get; set; }
        [Key, Column("other_id")] public int OtherId { get; set; }
        [Collection(nameof(CompositeItem.ParentId))] public List<CompositeItem> Items { get; set; } = [];
    }

    [Table("dbo", "items")]
    public class CompositeItem
    {
        [Column("parent_id"), ForeignKey(typeof(CompositeParent), nameof(CompositeParent.Id))]
        public int ParentId { get; set; }
    }

    [Table("dbo", "nodes")]
    public class Node
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("parent_id"), ForeignKey(typeof(Node), nameof(Id))] public int? ParentId { get; set; }
        [Collection(nameof(ParentId))] public List<Node> Children { get; set; } = [];
    }
}
