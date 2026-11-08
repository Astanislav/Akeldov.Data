using MongoDB.Bson;
using NUnit.Framework;

namespace Akeldov.Data.MongoDb.IntegrationTests;

public partial class MongoClientSelectTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_UsesAllKeysAndDeletesEveryMatchingDocument(bool asynchronous)
    {
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        client.Create(new UpdateRow { Id = id, TenantId = 10, Name = "delete first" });
        client.Create(new UpdateRow { Id = id, TenantId = 10, Name = "delete duplicate" });
        client.Create(new UpdateRow { Id = id, TenantId = 20, Name = "other tenant" });
        client.Create(new UpdateRow { Id = otherId, TenantId = 10, Name = "other row" });
        var entity = new DeleteModel { Id = id, TenantId = 10 };

        if (asynchronous) await client.DeleteAsync(entity);
        else client.Delete(entity);

        Assert.That(client.Select<UpdateRow>(row => row.Id == id && row.TenantId == 10), Is.Empty);
        Assert.That(client.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("other tenant"));
        Assert.That(client.Select<UpdateRow>(row => row.Id == otherId).Single().Name, Is.EqualTo("other row"));
        Assert.That(entity.Id, Is.EqualTo(id));
        Assert.That(entity.TenantId, Is.EqualTo(10));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_NoMatchingKey_LeavesDocumentsUnchanged(bool asynchronous)
    {
        var id = Guid.NewGuid();
        client.Create(new UpdateRow { Id = id, TenantId = 10, Name = "unchanged" });
        var missing = new DeleteModel { Id = id, TenantId = 20 };
        if (asynchronous) await client.DeleteAsync(missing);
        else client.Delete(missing);
        Assert.That(client.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("unchanged"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_DoesNotTraverseReferences(bool asynchronous)
    {
        var region = new Region { Id = ObjectId.GenerateNewId() };
        var customer = new Customer { Id = Guid.NewGuid(), Region = region };
        var order = new Order { Id = Guid.NewGuid(), Buyer = customer, Recipient = customer };
        client.Create(order);
        if (asynchronous) await client.DeleteAsync(order);
        else client.Delete(order);
        Assert.That(client.Select<Order>(row => row.Id == order.Id), Is.Empty);
        Assert.That(client.Select<Customer>(row => row.Id == customer.Id), Has.Count.EqualTo(1));
        Assert.That(client.Select<Region>(row => row.Id == region.Id), Has.Count.EqualTo(1));
        Assert.That(order.BuyerId, Is.EqualTo(customer.Id));
        Assert.That(order.RecipientId, Is.EqualTo(customer.Id));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_KeyOnlyObjectIdModel_IsSupported(bool asynchronous)
    {
        var entity = new Region { Id = ObjectId.GenerateNewId() };
        var other = new Region { Id = ObjectId.GenerateNewId() };
        client.Create(entity);
        client.Create(other);
        if (asynchronous) await client.DeleteAsync(entity);
        else client.Delete(entity);
        Assert.That(client.Select<Region>(row => row.Id == entity.Id), Is.Empty);
        Assert.That(client.Select<Region>(row => row.Id == other.Id), Has.Count.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_StringKeyContainingOperators_RemainsLiteral(bool asynchronous)
    {
        var entity = new StringDeleteKey { Id = $"{Guid.NewGuid()}: {{'$ne': null}}; db.rows.drop()" };
        var other = new StringDeleteKey { Id = Guid.NewGuid().ToString() };
        client.Create(entity);
        client.Create(other);
        if (asynchronous) await client.DeleteAsync(entity);
        else client.Delete(entity);
        Assert.That(client.Select<StringDeleteKey>(row => row.Id == entity.Id), Is.Empty);
        Assert.That(client.Select<StringDeleteKey>(row => row.Id == other.Id), Has.Count.EqualTo(1));
    }

    [Test]
    public void DeleteAsync_PreCanceled_LeavesDocumentUnchanged()
    {
        var id = Guid.NewGuid();
        client.Create(new UpdateRow { Id = id, TenantId = 10, Name = "unchanged" });
        Assert.That(async () => await client.DeleteAsync(new DeleteModel { Id = id, TenantId = 10 }, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(client.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("unchanged"));
    }

    [Table(DatabaseName, "update_rows")]
    public class DeleteModel
    {
        [Key, Column("business_id")] public Guid Id { get; set; }
        [Key, Column("tenant_id")] public int TenantId { get; set; }
    }

    [Table(DatabaseName, "delete_string_keys")]
    public class StringDeleteKey { [Key, Column("_id")] public string Id { get; set; } = ""; }
}
