using System.Globalization;
using System.Linq.Expressions;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Exceptions;
using Kusto.Data.Net.Client;
using NUnit.Framework;
using Testcontainers.Kusto;

namespace Akeldov.Data.Kusto.IntegrationTests;

/// <summary>Runs the public select API against the real Kusto engine in an isolated Docker container.</summary>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class KustoQueryProviderSelectTests
{
    private const string Database = "integration";
    private const string EmulatorImage = "mcr.microsoft.com/azuredataexplorer/kustainer-linux:stable";
    private const string ParameterValue = "Robert'); users | take 1 //";
    private KustoContainer? container;
    private ICslQueryProvider? provider;

    [OneTimeSetUp]
    public async Task StartDatabase()
    {
        try
        {
            container = new KustoBuilder(EmulatorImage)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .Build();
            // Includes the initial image download; subsequent runs use Docker's image cache.
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await container.StartAsync(cancellation.Token);
            var connection = new KustoConnectionStringBuilder(container.GetConnectionString());
            using var admin = KustoClientFactory.CreateCslAdminProvider(connection);
            await ExecuteCommand(admin, "NetDefaultDB", """
                .create database integration persist (
                    @"/kustodata/dbs/integration/md",
                    @"/kustodata/dbs/integration/data"
                )
                """, cancellation.Token);

            // Synchronous direct ingestion avoids queued ingestion and makes rows visible before tests run.
            await ExecuteCommand(admin, Database, """
                .set users <| datatable(
                    user_id:int, display_name:string, score:int, other_score:int, is_active:bool,
                    state:int, external_id:guid, created:datetime, duration:timespan,
                    measurement:real, count:long, extra_column:string)
                [
                    1, "Alice", 10, 10, true, 1, guid(00000000-0000-0000-0000-000000000001), datetime(2026-01-01), timespan(00:01:00), 1.5, 5000000001, "unmapped",
                    2, "Bob", 20, int(null), false, 0, guid(00000000-0000-0000-0000-000000000002), datetime(2026-01-02), timespan(00:02:00), 2.5, 5000000002, "unmapped",
                    3, "", int(null), int(null), false, 0, guid(00000000-0000-0000-0000-000000000003), datetime(2026-01-03), timespan(00:03:00), 3.5, 5000000003, "unmapped",
                    4, "Robert'); users | take 1 //", 0, 10, true, 1, guid(00000000-0000-0000-0000-000000000004), datetime(2026-01-04), timespan(00:04:00), 4.5, 5000000004, "unmapped",
                    5, "Ольга", int(null), 20, true, 1, guid(00000000-0000-0000-0000-000000000005), datetime(2026-01-05), timespan(00:05:00), 5.5, 5000000005, "unmapped"
                ]
                """, cancellation.Token);
            await ExecuteCommand(admin, Database,
                ".create table empty_users (user_id:int)", cancellation.Token);
            await ExecuteCommand(admin, Database,
                ".set ['odd table'] <| datatable(['odd-id']:int)[42]", cancellation.Token);
            await ExecuteCommand(admin, Database,
                ".set case_rows <| datatable(['Value']:int, ['value']:int)[1, 2]", cancellation.Token);
            await ExecuteCommand(admin, Database,
                ".set parameter_collision <| datatable(akeldov_p0:int, akeldov_p1:int)[42, 43]", cancellation.Token);
            provider = KustoClientFactory.CreateCslQueryProvider(connection);
        }
        catch
        {
            await DisposeResources();
            throw;
        }
    }

    [OneTimeTearDown]
    public Task StopDatabase() => DisposeResources();

    [Test]
    public void Select_ReadsAllRowsAndMappedTypes()
        => AssertRows(Client.Select<User>());

    [Test]
    public async Task SelectAsync_ReadsAllRowsAndMappedTypes()
        => AssertRows(await Client.SelectAsync<User>());

    [TestCaseSource(nameof(Predicates))]
    public void Select_WithPredicate_MatchesCSharpEvaluation(Expression<Func<User, bool>> predicate)
    {
        var expected = SeedRows().Where(predicate.Compile()).Select(row => row.Id);
        var actual = Client.Select(predicate).Select(row => row.Id);
        Assert.That(actual, Is.EquivalentTo(expected));
    }

    [TestCaseSource(nameof(Predicates))]
    public async Task SelectAsync_WithPredicate_MatchesCSharpEvaluation(Expression<Func<User, bool>> predicate)
    {
        var expected = SeedRows().Where(predicate.Compile()).Select(row => row.Id);
        var actual = (await Client.SelectAsync(predicate)).Select(row => row.Id);
        Assert.That(actual, Is.EquivalentTo(expected));
    }

    [Test]
    public void Select_ParameterContainingKql_ReturnsExactMatchAndLeavesTableIntact()
    {
        var name = ParameterValue;
        var actual = Client.Select<User>(row => row.Name == name);
        Assert.That(actual.Select(row => row.Id), Is.EqualTo(new[] { 4 }));
        Assert.That(Client.Select<User>(), Has.Count.EqualTo(5));
    }

    [Test]
    public async Task SelectAsync_ParameterContainingKql_ReturnsExactMatchAndLeavesTableIntact()
    {
        var name = ParameterValue;
        var actual = await Client.SelectAsync<User>(row => row.Name == name);
        Assert.That(actual.Select(row => row.Id), Is.EqualTo(new[] { 4 }));
        Assert.That(await Client.SelectAsync<User>(), Has.Count.EqualTo(5));
    }

    [Test]
    public void Select_EmptyTable_ReturnsEmptyList()
        => Assert.That(Client.Select<EmptyRow>(), Is.Empty);

    [Test]
    public async Task SelectAsync_EmptyTable_ReturnsEmptyList()
        => Assert.That(await Client.SelectAsync<EmptyRow>(), Is.Empty);

    [Test]
    public void Select_QuotedIdentifiersAndCaseDistinctColumns_AreResolved()
    {
        Assert.That(Client.Select<EscapedRow>(row => row.Id == 42).Single().Id, Is.EqualTo(42));
        var row = Client.Select<CaseRow>(row => row.Upper != row.Lower).Single();
        Assert.That(row.Upper, Is.EqualTo(1));
        Assert.That(row.Lower, Is.EqualTo(2));
    }

    [Test]
    public void Select_ParameterNamesCollidingWithColumns_AreResolved()
    {
        var row = Client.Select<CollisionRow>(row => row.First == 42 && row.Second == 43).Single();
        Assert.That(row.First, Is.EqualTo(42));
        Assert.That(row.Second, Is.EqualTo(43));
    }

    [Test]
    public void Select_KustoDatetime_MapsToDateTimeOffsetInUtc()
    {
        var threshold = new DateTimeOffset(2026, 1, 3, 3, 0, 0, TimeSpan.FromHours(3));
        var rows = Client.Select<OffsetRow>(row => row.Created >= threshold).OrderBy(row => row.Created).ToArray();
        Assert.That(rows, Has.Length.EqualTo(3));
        Assert.That(rows[0].Created, Is.EqualTo(threshold.ToUniversalTime()));
        Assert.That(rows.All(row => row.Created.Offset == TimeSpan.Zero), Is.True);
    }

    [Test]
    public void Select_CapturedValues_UseCurrentValueOnEachQuery()
    {
        var settings = new FilterSettings { MinimumId = 4 };
        Expression<Func<User, bool>> predicate = row => row.Id >= settings.MinimumId;
        Assert.That(Client.Select(predicate).Select(row => row.Id), Is.EquivalentTo(new[] { 4, 5 }));
        settings.MinimumId = 5;
        Assert.That(Client.Select(predicate).Select(row => row.Id), Is.EqualTo(new[] { 5 }));
    }

    [Test]
    public void Select_RealParameter_UsesInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var threshold = 3.5;
            Assert.That(Client.Select<User>(row => row.Measurement >= threshold).Select(row => row.Id),
                Is.EquivalentTo(new[] { 3, 4, 5 }));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public void Select_MissingTable_PropagatesKustoErrorAndProviderCanBeReused()
    {
        Assert.Catch<KustoException>(() => Client.Select<MissingTableRow>());
        Assert.That(Client.Select<User>(), Has.Count.EqualTo(5));
    }

    [Test]
    public void SelectAsync_MissingTable_PropagatesKustoErrorAndProviderCanBeReused()
    {
        Assert.CatchAsync<KustoException>(async () => await Client.SelectAsync<MissingTableRow>());
        Assert.That(Client.Select<User>(), Has.Count.EqualTo(5));
    }

    [Test]
    public void Select_UnsupportedPredicate_ThrowsAndProviderCanBeReused()
    {
        Assert.That(() => Client.Select<User>(row => row.Name.StartsWith("A")), Throws.TypeOf<NotSupportedException>());
        Assert.That(Client.Select<User>(), Has.Count.EqualTo(5));
    }

    [Test]
    public void SelectAsync_CanceledToken_ThrowsAndProviderCanBeReused()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.That(async () => await Client.SelectAsync<User>(row => row.Id > 0, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Client.Select<User>(), Has.Count.EqualTo(5));
    }

    private ICslQueryProvider Client => provider ?? throw new InvalidOperationException("Kusto is not initialized.");

    private static async Task ExecuteCommand(ICslAdminProvider admin, string database, string command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var properties = new ClientRequestProperties();
        properties.SetOption(ClientRequestProperties.OptionServerTimeout, TimeSpan.FromMinutes(2));
        using var result = await admin.ExecuteControlCommandAsync(database, command, properties);
        // Consume the response so ingestion and server errors are observed before continuing.
        while (result.Read()) { }
        token.ThrowIfCancellationRequested();
    }

    private async Task DisposeResources()
    {
        try
        {
            provider?.Dispose();
            provider = null;
        }
        finally
        {
            if (container is not null)
            {
                await container.DisposeAsync();
                container = null;
            }
        }
    }

    private static void AssertRows(List<User> rows)
    {
        var actual = rows.OrderBy(row => row.Id).ToArray();
        var expected = SeedRows();
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
                Assert.That(row.Duration, Is.EqualTo(seed.Duration));
                Assert.That(row.Measurement, Is.EqualTo(seed.Measurement));
                Assert.That(row.Count, Is.EqualTo(seed.Count));
                Assert.That(row.Unmapped, Is.EqualTo("default"));
            });
        }
    }

    private static User[] SeedRows()
    {
        string[] names = ["Alice", "Bob", "", ParameterValue, "Ольга"];
        int?[] scores = [10, 20, null, 0, null];
        int?[] otherScores = [10, null, null, 10, 20];
        return Enumerable.Range(1, 5).Select(id => new User
        {
            Id = id,
            Name = names[id - 1],
            Score = scores[id - 1],
            OtherScore = otherScores[id - 1],
            Active = id is 1 or 4 or 5,
            State = id is 1 or 4 or 5 ? UserState.Active : UserState.Inactive,
            ExternalId = Guid.Parse($"00000000-0000-0000-0000-{id:D12}"),
            Created = new DateTime(2026, 1, id, 0, 0, 0, DateTimeKind.Utc),
            Duration = TimeSpan.FromMinutes(id),
            Measurement = id + 0.5,
            Count = 5_000_000_000L + id
        }).ToArray();
    }

    private static IEnumerable<TestCaseData> Predicates()
    {
        var minimum = 1;
        int? score = null;
        var externalId = SeedRows()[0].ExternalId;
        var created = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var duration = TimeSpan.FromMinutes(3);
        Expression<Func<User, bool>>[] predicates =
        [
            row => row.Id == 2,
            row => row.Id != 2,
            row => row.Id > 2,
            row => row.Id >= 2,
            row => row.Id < 2,
            row => row.Id <= 2,
            row => row.Id > minimum && row.Name != null,
            row => (row.Id < 3 || row.Id == 5) && !row.Active,
            row => row.Active,
            row => row.Name == null,
            row => row.Name == "",
            row => row.Name == "alice",
            row => row.Name == "Ольга",
            row => row.Score == score,
            row => row.Score != null,
            row => row.Score != 10,
            row => !(row.Score > 10),
            row => row.Score == row.OtherScore,
            row => row.Score != row.OtherScore,
            row => !(row.Score == row.OtherScore),
            row => !(row.Score != row.OtherScore),
            row => row.State == UserState.Active,
            row => row.ExternalId == externalId,
            row => row.Created >= created,
            row => row.Duration < duration,
            row => row.Measurement >= 3.5,
            row => row.Count > 5_000_000_003L,
            row => row.Id > 100,
            row => true,
            row => false
        ];
        return predicates.Select(predicate => new TestCaseData(predicate)
            .SetArgDisplayNames(predicate.ToString()));
    }

    [Table(Database, "users")]
    public class User
    {
        [Column("user_id")] public int Id { get; set; }
        [Column("display_name")] public string Name { get; set; } = "";
        [Column("score")] public int? Score { get; set; }
        [Column("other_score")] public int? OtherScore { get; set; }
        [Column("is_active")] public bool Active { get; set; }
        [Column("state")] public UserState State { get; set; }
        [Column("external_id")] public Guid ExternalId { get; set; }
        [Column("created")] public DateTime Created { get; set; }
        [Column("duration")] public TimeSpan Duration { get; set; }
        [Column("measurement")] public double Measurement { get; set; }
        [Column("count")] public long Count { get; set; }
        public string Unmapped { get; set; } = "default";
    }

    public enum UserState { Inactive, Active }
    public class FilterSettings { public int MinimumId { get; set; } }

    [Table(Database, "empty_users")]
    public class EmptyRow { [Column("user_id")] public int Id { get; set; } }

    [Table(Database, "odd table")]
    public class EscapedRow { [Column("odd-id")] public int Id { get; set; } }

    [Table(Database, "case_rows")]
    public class CaseRow
    {
        [Column("Value")] public int Upper { get; set; }
        [Column("value")] public int Lower { get; set; }
    }

    [Table(Database, "parameter_collision")]
    public class CollisionRow
    {
        [Column("akeldov_p0")] public int First { get; set; }
        [Column("akeldov_p1")] public int Second { get; set; }
    }

    [Table(Database, "users")]
    public class OffsetRow { [Column("created")] public DateTimeOffset Created { get; set; } }

    [Table(Database, "missing_table")]
    public class MissingTableRow { [Column("user_id")] public int Id { get; set; } }
}
