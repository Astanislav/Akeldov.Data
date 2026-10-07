using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.ADO.Parameters;
using NUnit.Framework;
using Testcontainers.ClickHouse;

namespace Akeldov.Data.ClickHouse.IntegrationTests;

/// <summary>Runs the public API against an isolated ClickHouse server. Requires Docker with Linux containers.</summary>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class ClickHouseConnectionSelectTests
{
    private ClickHouseContainer? container;
    private string connectionString = null!;
    private const string ParameterValue = "Robert'); DROP TABLE analytics.users;-- Ольга";
    private static readonly DateTime Created = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
    private static readonly Guid Uuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    [OneTimeSetUp]
    public async Task StartDatabase()
    {
        container = new ClickHouseBuilder("clickhouse/clickhouse-server:25.8").WithDatabase("analytics").Build();
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await container.StartAsync(cancellation.Token);
            connectionString = container.GetConnectionString();
            using var connection = new ClickHouseConnection(connectionString);
            await connection.OpenAsync(cancellation.Token);
            await Execute(connection, """
                CREATE TABLE analytics.users
                (
                    user_id Int32, name Nullable(String), score Nullable(Int32), other_score Nullable(Int32),
                    is_active Bool, state Int32, external_id UUID, created DateTime64(7, 'UTC'), amount Decimal(18, 4),
                    extra String
                ) ENGINE = MergeTree ORDER BY user_id
                """, cancellation.Token);
            await Execute(connection, "CREATE TABLE analytics.empty_users (user_id Int32) ENGINE = Memory", cancellation.Token);
            await Execute(connection, "CREATE TABLE analytics.`odd\\`table` (`odd\\`id` Int32) ENGINE = Memory", cancellation.Token);
            await Execute(connection, "INSERT INTO analytics.`odd\\`table` VALUES (42)", cancellation.Token);
            await Execute(connection, "CREATE TABLE analytics.case_rows (id Int32, ID Int32) ENGINE = Memory", cancellation.Token);
            await Execute(connection, "INSERT INTO analytics.case_rows VALUES (1, 2)", cancellation.Token);

            foreach (var row in SeedRows())
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    INSERT INTO analytics.users VALUES
                    ({row.Id}, @name, {(row.Score?.ToString(CultureInfo.InvariantCulture) ?? "NULL")},
                     {(row.OtherScore?.ToString(CultureInfo.InvariantCulture) ?? "NULL")}, {(row.Active ? 1 : 0)},
                     {(int)row.State}, @id, @created, @amount, 'unmapped')
                    """;
                command.Parameters.Add(new ClickHouseDbParameter { ParameterName = "name", ClickHouseType = "Nullable(String)", Value = (object?)row.Name ?? DBNull.Value });
                command.Parameters.Add(new ClickHouseDbParameter { ParameterName = "id", ClickHouseType = "UUID", Value = row.ExternalId });
                command.Parameters.Add(new ClickHouseDbParameter { ParameterName = "created", ClickHouseType = "DateTime64(7, 'UTC')", Value = row.Created });
                command.Parameters.Add(new ClickHouseDbParameter { ParameterName = "amount", ClickHouseType = "Decimal(18, 4)", Value = row.Amount });
                await command.ExecuteNonQueryAsync(cancellation.Token);
            }
        }
        catch
        {
            await container.DisposeAsync();
            container = null;
            throw;
        }
    }

    [OneTimeTearDown]
    public async Task StopDatabase()
    {
        if (container is not null) await container.DisposeAsync();
    }

    [Test]
    public void Select_ReadsMappedTypes()
    {
        using var connection = new ClickHouseConnection(connectionString);
        AssertRows(connection.Select<User>());
    }

    [Test]
    public async Task SelectAsync_ReadsMappedTypes()
    {
        using var connection = new ClickHouseConnection(connectionString);
        AssertRows(await connection.SelectAsync<User>());
    }

    [TestCaseSource(nameof(Predicates))]
    public void Select_Predicate_MatchesCSharpResult(Expression<Func<User, bool>> predicate)
    {
        using var connection = new ClickHouseConnection(connectionString);
        Assert.That(connection.Select(predicate).Select(row => row.Id),
            Is.EquivalentTo(SeedRows().Where(predicate.Compile()).Select(row => row.Id)));
    }

    [Test]
    public async Task SelectAsync_ParameterizedText_IsSafeAndExact()
    {
        using var connection = new ClickHouseConnection(connectionString);
        var value = ParameterValue;
        Assert.That((await connection.SelectAsync<User>(row => row.Name == value)).Single().Id, Is.EqualTo(4));
        Assert.That(await connection.SelectAsync<User>(), Has.Count.EqualTo(5));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SelectAsync_PreservesConnectionState(bool initiallyOpen)
    {
        using var connection = new ClickHouseConnection(connectionString);
        if (initiallyOpen) await connection.OpenAsync();
        await connection.SelectAsync<User>(row => row.Id == 1);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Select_QueryFailure_PreservesConnectionState(bool initiallyOpen)
    {
        using var connection = new ClickHouseConnection(connectionString);
        if (initiallyOpen) connection.Open();
        Assert.That(() => connection.Select<MissingTable>(), Throws.Exception);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    [Test]
    public void Select_EscapedAndCaseSensitiveNames_Work()
    {
        using var connection = new ClickHouseConnection(connectionString);
        Assert.That(connection.Select<EscapedRow>(row => row.Id == 42).Single().Id, Is.EqualTo(42));
        Assert.That(connection.Select<CaseRow>(row => row.Id == 1 && row.OtherId == 2).Single().OtherId, Is.EqualTo(2));
    }

    [Test]
    public async Task SelectAsync_EmptyTable_ReturnsEmptyList()
    {
        using var connection = new ClickHouseConnection(connectionString);
        Assert.That(await connection.SelectAsync<EmptyRow>(), Is.Empty);
    }

    private static async Task Execute(ClickHouseConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AssertRows(List<User> rows)
    {
        var expected = SeedRows();
        var actual = rows.OrderBy(row => row.Id).ToArray();
        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (var index = 0; index < expected.Length; index++)
        {
            var row = actual[index];
            var seed = expected[index];
            Assert.Multiple(() =>
            {
                Assert.That(row.Id, Is.EqualTo(seed.Id));
                Assert.That(row.Name, Is.EqualTo(seed.Name));
                Assert.That(row.Score, Is.EqualTo(seed.Score));
                Assert.That(row.OtherScore, Is.EqualTo(seed.OtherScore));
                Assert.That(row.Active, Is.EqualTo(seed.Active));
                Assert.That(row.State, Is.EqualTo(seed.State));
                Assert.That(row.ExternalId, Is.EqualTo(seed.ExternalId));
                Assert.That(row.Created, Is.EqualTo(seed.Created));
                Assert.That(row.Amount, Is.EqualTo(seed.Amount));
                Assert.That(row.Unmapped, Is.EqualTo("default"));
            });
        }
    }

    private static User[] SeedRows()
    {
        string?[] names = ["Alice", "Bob", null, ParameterValue, "Ольга"];
        int?[] scores = [10, 20, null, 0, null];
        int?[] otherScores = [10, null, null, 10, 20];
        return Enumerable.Range(1, 5).Select(id => new User
        {
            Id = id, Name = names[id - 1], Score = scores[id - 1], OtherScore = otherScores[id - 1],
            Active = id is 1 or 4 or 5, State = id is 1 or 4 or 5 ? UserState.Active : UserState.Inactive,
            ExternalId = Uuid, Created = Created.AddDays(id - 1), Amount = 10.1234m + id
        }).ToArray();
    }

    private static IEnumerable<TestCaseData> Predicates()
    {
        var minimum = 2;
        var date = Created.AddDays(2);
        var id = Uuid;
        Expression<Func<User, bool>>[] predicates =
        [
            row => row.Id == 2, row => row.Id != 2, row => row.Id > minimum,
            row => row.Id >= 2, row => row.Id < 3, row => row.Id <= 3,
            row => row.Id > 1 && row.Name != null, row => (row.Id < 3 || row.Id == 5) && !row.Active,
            row => row.Active, row => row.Name == null, row => row.Score == null,
            row => row.Score != 10, row => !(row.Score > 10),
            row => row.Score == row.OtherScore, row => row.Score != row.OtherScore,
            row => !(row.Score == row.OtherScore), row => row.State == UserState.Active,
            row => row.ExternalId == id, row => row.Created >= date, row => row.Amount == 11.1234m,
            row => row.Name == "Ольга"
        ];
        return predicates.Select(predicate => new TestCaseData(predicate).SetName($"Select ClickHouse: {predicate}"));
    }

    [Table("analytics", "users")]
    public class User
    {
        [Column("user_id")] public int Id { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("other_score")] public int? OtherScore { get; set; }
        [Column("is_active")] public bool Active { get; set; }
        [Column("state")] public UserState State { get; set; }
        [Column("external_id")] public Guid ExternalId { get; set; }
        [Column("created")] public DateTime Created { get; set; }
        [Column("amount")] public decimal Amount { get; set; }
        public string Unmapped { get; set; } = "default";
    }
    public enum UserState { Inactive, Active }
    [Table("analytics", "odd`table")]
    public class EscapedRow { [Column("odd`id")] public int Id { get; set; } }
    [Table("analytics", "case_rows")]
    public class CaseRow
    {
        [Column("id")] public int Id { get; set; }
        [Column("ID")] public int OtherId { get; set; }
    }
    [Table("analytics", "empty_users")]
    public class EmptyRow { [Column("user_id")] public int Id { get; set; } }
    [Table("analytics", "missing_table")]
    public class MissingTable { [Column("user_id")] public int Id { get; set; } }
}
