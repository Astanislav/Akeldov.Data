using Akeldov.Data.MongoDb;
using MongoDB.Bson;
using NUnit.Framework;

namespace Akeldov.Data.MongoDb.IntegrationTests;

public partial class MongoClientSelectTests
{
    [Test]
    public async Task Update_UsesAllKeysAndUpdatesEveryMatchingDocument([Values] bool asynchronous)
    {
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        client.Create(new UpdateRow { Id = id, TenantId = 10, Name = "before one", Score = 5, Extra = "one" });
        client.Create(new UpdateRow { Id = id, TenantId = 10, Name = "before two", Score = 6, Extra = "two" });
        client.Create(new UpdateRow { Id = id, TenantId = 20, Name = "other tenant" });
        client.Create(new UpdateRow { Id = otherId, TenantId = 10, Name = "other row" });
        var entity = new UpdateModel
        {
            Id = id, TenantId = 10, Name = "O'Brien'); db.update_rows.drop();--", Score = null,
            Active = true, Created = DateTime.UnixEpoch.AddMilliseconds(123), Payload = [0, 1, 255]
        };

        if (asynchronous) await client.UpdateAsync(entity);
        else client.Update(entity);

        var actual = client.Select<UpdateRow>(row => row.Id == id && row.TenantId == 10);
        Assert.That(actual, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(actual.Select(row => row.Name), Is.All.EqualTo(entity.Name));
            Assert.That(actual.Select(row => row.Score), Is.All.Null);
            Assert.That(actual.Select(row => row.Active), Is.All.True);
            Assert.That(actual.Select(row => row.Created), Is.All.EqualTo(entity.Created));
            Assert.That(actual.Select(row => row.Payload), Is.All.EqualTo(entity.Payload));
            Assert.That(actual.Select(row => row.Extra), Is.EquivalentTo(new[] { "one", "two" }));
        });
        Assert.That(client.Select<UpdateRow>(row => row.Id == id && row.TenantId == 20).Single().Name,
            Is.EqualTo("other tenant"));
        Assert.That(client.Select<UpdateRow>(row => row.Id == otherId).Single().Name, Is.EqualTo("other row"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Update_NoMatchingKey_DoesNotInsert(bool asynchronous)
    {
        var id = Guid.NewGuid();
        var entity = new UpdateModel { Id = id, TenantId = 10, Name = "missing" };
        if (asynchronous) await client.UpdateAsync(entity);
        else client.Update(entity);
        Assert.That(client.Select<UpdateRow>(row => row.Id == id), Is.Empty);
    }

    [Test]
    public void UpdateAsync_PreCanceled_DoesNotChangeExistingDocument()
    {
        var id = Guid.NewGuid();
        client.Create(new UpdateRow { Id = id, TenantId = 10, Name = "unchanged" });
        Assert.That(async () => await client.UpdateAsync(
            new UpdateModel { Id = id, TenantId = 10, Name = "canceled" }, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(client.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("unchanged"));
    }

    [Table(DatabaseName, "update_rows")]
    public class UpdateModel
    {
        [Key, Column("business_id")] public Guid Id { get; set; }
        [Key, Column("tenant_id")] public int TenantId { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("active")] public bool Active { get; set; }
        [Column("created")] public DateTime Created { get; set; } = DateTime.UnixEpoch;
        [Column("payload")] public byte[]? Payload { get; set; }
    }

    [Table(DatabaseName, "update_rows")]
    public class UpdateRow
    {
        [Key, Column("business_id")] public Guid Id { get; set; }
        [Key, Column("tenant_id")] public int TenantId { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("active")] public bool Active { get; set; }
        [Column("created")] public DateTime Created { get; set; } = DateTime.UnixEpoch;
        [Column("payload")] public byte[]? Payload { get; set; }
        [Column("extra")] public string Extra { get; set; } = "";
    }
}
