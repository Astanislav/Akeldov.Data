using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using NUnit.Framework;

namespace Akeldov.Data.Kusto.IntegrationTests;

public partial class KustoQueryProviderSelectTests
{
    [Test]
    public async Task Update_UsesAllKeysAndRoundTripsMappedValues([Values] bool asynchronous)
    {
        using var admin = CreateAdminProvider();
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        admin.Create(new UpdateRow { Id = id, TenantId = 10, Name = "before", Score = 5, Extra = "preserved" });
        admin.Create(new UpdateRow { Id = id, TenantId = 20, Name = "other tenant" });
        admin.Create(new UpdateRow { Id = otherId, TenantId = 10, Name = "other row" });
        var entity = new UpdateModel
        {
            Id = id, TenantId = 10, Name = "O'Brien\n.update table other <|", Score = null,
            Active = true, Created = DateTime.UnixEpoch.AddTicks(1234567)
        };

        if (asynchronous) await admin.UpdateAsync(entity);
        else admin.Update(entity);

        var actual = Client.Select<UpdateRow>(row => row.Id == id && row.TenantId == 10).Single();
        Assert.Multiple(() =>
        {
            Assert.That(actual.Name, Is.EqualTo(entity.Name));
            Assert.That(actual.Score, Is.Null);
            Assert.That(actual.Active, Is.True);
            Assert.That(actual.Created, Is.EqualTo(entity.Created));
            Assert.That(actual.Extra, Is.EqualTo("preserved"));
        });
        Assert.That(Client.Select<UpdateRow>(row => row.Id == id && row.TenantId == 20).Single().Name,
            Is.EqualTo("other tenant"));
        Assert.That(Client.Select<UpdateRow>(row => row.Id == otherId).Single().Name, Is.EqualTo("other row"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Update_NoMatchingKey_DoesNotInsert(bool asynchronous)
    {
        using var admin = CreateAdminProvider();
        var id = Guid.NewGuid();
        var entity = new UpdateModel { Id = id, TenantId = 10, Name = "missing" };
        if (asynchronous) await admin.UpdateAsync(entity);
        else admin.Update(entity);
        Assert.That(Client.Select<UpdateRow>(row => row.Id == id), Is.Empty);
    }

    [Test]
    public void UpdateAsync_PreCanceled_DoesNotChangeExistingRow()
    {
        using var admin = CreateAdminProvider();
        var id = Guid.NewGuid();
        admin.Create(new UpdateRow { Id = id, TenantId = 10, Name = "unchanged" });
        Assert.That(async () => await admin.UpdateAsync(
            new UpdateModel { Id = id, TenantId = 10, Name = "canceled" }, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Client.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("unchanged"));
    }

    private ICslAdminProvider CreateAdminProvider() => KustoClientFactory.CreateCslAdminProvider(
        new KustoConnectionStringBuilder(container!.GetConnectionString()));

    [Table(Database, "update_rows")]
    public class UpdateModel
    {
        [Key, Column("id")] public Guid Id { get; set; }
        [Key, Column("tenant_id")] public int TenantId { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("active")] public bool Active { get; set; }
        [Column("created")] public DateTime Created { get; set; } = DateTime.UnixEpoch;
    }

    [Table(Database, "update_rows")]
    public class UpdateRow
    {
        [Key, Column("id")] public Guid Id { get; set; }
        [Key, Column("tenant_id")] public int TenantId { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("active")] public bool Active { get; set; }
        [Column("created")] public DateTime Created { get; set; } = DateTime.UnixEpoch;
        [Column("extra")] public string Extra { get; set; } = "";
    }
}
