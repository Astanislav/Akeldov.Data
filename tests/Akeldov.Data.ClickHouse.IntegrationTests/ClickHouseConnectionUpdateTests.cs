using System.Data;
using ClickHouse.Driver.ADO;
using NUnit.Framework;

namespace Akeldov.Data.ClickHouse.IntegrationTests;

public partial class ClickHouseConnectionSelectTests
{
    [Test]
    public async Task Update_UsesAllKeysAndRoundTripsMappedValues(
        [Values] bool asynchronous, [Values] bool initiallyOpen)
    {
        using var connection = new ClickHouseConnection(connectionString);
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        connection.Create(new UpdateRow { Id = id, TenantId = 10, Name = "before", Score = 5, Extra = "preserved" });
        connection.Create(new UpdateRow { Id = id, TenantId = 20, Name = "other tenant" });
        connection.Create(new UpdateRow { Id = otherId, TenantId = 10, Name = "other row" });
        if (initiallyOpen) await connection.OpenAsync();
        var entity = new UpdateModel
        {
            Id = id, TenantId = 10, Name = "O'Brien'); DROP TABLE update_rows;--", Score = null,
            Active = true, Created = DateTime.UnixEpoch.AddTicks(1234567), Amount = 12.3456m
        };

        if (asynchronous) await connection.UpdateAsync(entity);
        else connection.Update(entity);

        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
        var actual = connection.Select<UpdateRow>(row => row.Id == id && row.TenantId == 10).Single();
        Assert.Multiple(() =>
        {
            Assert.That(actual.Name, Is.EqualTo(entity.Name));
            Assert.That(actual.Score, Is.Null);
            Assert.That(actual.Active, Is.True);
            Assert.That(actual.Created, Is.EqualTo(entity.Created));
            Assert.That(actual.Amount, Is.EqualTo(entity.Amount));
            Assert.That(actual.Extra, Is.EqualTo("preserved"));
        });
        Assert.That(connection.Select<UpdateRow>(row => row.Id == id && row.TenantId == 20).Single().Name,
            Is.EqualTo("other tenant"));
        Assert.That(connection.Select<UpdateRow>(row => row.Id == otherId).Single().Name, Is.EqualTo("other row"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Update_NoMatchingKey_DoesNotInsert(bool asynchronous)
    {
        using var connection = new ClickHouseConnection(connectionString);
        var id = Guid.NewGuid();
        var entity = new UpdateModel { Id = id, TenantId = 10, Name = "missing" };
        if (asynchronous) await connection.UpdateAsync(entity);
        else connection.Update(entity);
        Assert.That(connection.Select<UpdateRow>(row => row.Id == id), Is.Empty);
    }

    [Test]
    public void UpdateAsync_PreCanceled_DoesNotChangeExistingRow()
    {
        using var connection = new ClickHouseConnection(connectionString);
        var id = Guid.NewGuid();
        connection.Create(new UpdateRow { Id = id, TenantId = 10, Name = "unchanged" });
        Assert.That(async () => await connection.UpdateAsync(
            new UpdateModel { Id = id, TenantId = 10, Name = "canceled" }, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(connection.Select<UpdateRow>(row => row.Id == id).Single().Name, Is.EqualTo("unchanged"));
    }

    [Table("analytics", "update_rows")]
    public class UpdateModel
    {
        [Key, Column("id")] public Guid Id { get; set; }
        [Key, Column("tenant_id")] public int TenantId { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("active")] public bool Active { get; set; }
        [Column("created")] public DateTime Created { get; set; } = DateTime.UnixEpoch;
        [Column("amount")] public decimal Amount { get; set; }
    }

    [Table("analytics", "update_rows")]
    public class UpdateRow
    {
        [Key, Column("id")] public Guid Id { get; set; }
        [Key, Column("tenant_id")] public int TenantId { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("active")] public bool Active { get; set; }
        [Column("created")] public DateTime Created { get; set; } = DateTime.UnixEpoch;
        [Column("amount")] public decimal Amount { get; set; }
        [Column("extra")] public string Extra { get; set; } = "";
    }
}
