using Kusto.Data.Exceptions;
using NUnit.Framework;

namespace Akeldov.Data.Kusto.IntegrationTests;

public partial class KustoQueryProviderSelectTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_UsesAllKeysAndDeletesEveryMatchingRow(bool asynchronous)
    {
        using var admin = CreateAdminProvider();
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        admin.Create(new UpdateRow { Id = id, TenantId = 10, Name = "delete first" });
        admin.Create(new UpdateRow { Id = id, TenantId = 10, Name = "delete duplicate" });
        admin.Create(new UpdateRow { Id = id, TenantId = 20, Name = "other tenant" });
        admin.Create(new UpdateRow { Id = otherId, TenantId = 10, Name = "other row" });
        var entity = new DeleteModel { Id = id, TenantId = 10 };

        if (asynchronous) await admin.DeleteAsync(entity);
        else admin.Delete(entity);

        Assert.That(Client.Select<UpdateRow>(row => row.Id == id && row.TenantId == 10), Is.Empty);
        Assert.That(Client.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("other tenant"));
        Assert.That(Client.Select<UpdateRow>(row => row.Id == otherId).Single().Name, Is.EqualTo("other row"));
        Assert.That(entity.Id, Is.EqualTo(id));
        Assert.That(entity.TenantId, Is.EqualTo(10));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_NoMatchingKey_LeavesRowsUnchanged(bool asynchronous)
    {
        using var admin = CreateAdminProvider();
        var id = Guid.NewGuid();
        admin.Create(new UpdateRow { Id = id, TenantId = 10, Name = "unchanged" });
        var missing = new DeleteModel { Id = id, TenantId = 20 };
        if (asynchronous) await admin.DeleteAsync(missing);
        else admin.Delete(missing);
        Assert.That(Client.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("unchanged"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_DoesNotTraverseReferences(bool asynchronous)
    {
        using var admin = CreateAdminProvider();
        var customer = new Customer { Id = Guid.NewGuid() };
        var order = new Order { Id = Guid.NewGuid(), Buyer = customer, Recipient = customer };
        admin.Create(order);

        if (asynchronous) await admin.DeleteAsync(order);
        else admin.Delete(order);

        Assert.That(Client.Select<Order>(row => row.Id == order.Id), Is.Empty);
        Assert.That(Client.Select<Customer>(row => row.Id == customer.Id), Has.Count.EqualTo(1));
        Assert.That(order.BuyerId, Is.EqualTo(customer.Id));
        Assert.That(order.RecipientId, Is.EqualTo(customer.Id));
        // The caller-owned administration provider is still usable; key-only models work.
        if (asynchronous) await admin.DeleteAsync(customer);
        else admin.Delete(customer);
        Assert.That(Client.Select<Customer>(row => row.Id == customer.Id), Is.Empty);
    }

    [Test]
    public void DeleteAsync_PreCanceled_LeavesRowUnchanged()
    {
        using var admin = CreateAdminProvider();
        var id = Guid.NewGuid();
        admin.Create(new UpdateRow { Id = id, TenantId = 10, Name = "unchanged" });
        Assert.That(async () => await admin.DeleteAsync(new DeleteModel { Id = id, TenantId = 10 }, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Client.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("unchanged"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_ServerFailure_PropagatesAndProviderRemainsUsable(bool asynchronous)
    {
        using var admin = CreateAdminProvider();
        Assert.That(async () =>
        {
            if (asynchronous) await admin.DeleteAsync(new MissingDeleteRow { Id = Guid.NewGuid() });
            else admin.Delete(new MissingDeleteRow { Id = Guid.NewGuid() });
        }, Throws.InstanceOf<KustoException>());
        var customer = new Customer { Id = Guid.NewGuid() };
        admin.Create(customer);
        if (asynchronous) await admin.DeleteAsync(customer);
        else admin.Delete(customer);
        Assert.That(Client.Select<Customer>(row => row.Id == customer.Id), Is.Empty);
    }

    [Table(Database, "update_rows")]
    public class DeleteModel
    {
        [Key, Column("id")] public Guid Id { get; set; }
        [Key, Column("tenant_id")] public int TenantId { get; set; }
    }

    [Table(Database, "missing_delete_rows")]
    public class MissingDeleteRow { [Key, Column("id")] public Guid Id { get; set; } }
}
