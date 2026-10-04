using System.Data;
using System.Linq.Expressions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using Testcontainers.MsSql;

namespace Akeldov.Data.IntegrationTests;

/// <summary>
/// Requires a running Docker engine with Linux containers.
/// Each fixture owns a temporary SQL Server container and database.
/// </summary>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class SqlConnectionSelectTests
{
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    private const string ParameterValue = "Robert'); DROP TABLE [integration].[users];--";
    private MsSqlContainer? container;
    private string connectionString = null!;

    [OneTimeSetUp]
    public async Task StartDatabase()
    {
        container = new MsSqlBuilder(SqlServerImage).Build();

        try
        {
            // The first run also downloads the SQL Server image.
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await container.StartAsync(cancellation.Token);

            var databaseName = $"AkeldovDataTests_{Guid.NewGuid():N}";
            var builder = new SqlConnectionStringBuilder(container.GetConnectionString())
            {
                InitialCatalog = "master",
                Pooling = false
            };

            using (var master = new SqlConnection(builder.ConnectionString))
            {
                await master.OpenAsync(cancellation.Token);
                using var command = master.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{databaseName}]";
                await command.ExecuteNonQueryAsync(cancellation.Token);
            }

            builder.InitialCatalog = databaseName;
            connectionString = builder.ConnectionString;
            await ExecuteSql("CREATE SCHEMA [integration]");
            await ExecuteSql("""
                CREATE TABLE [integration].[users]
                (
                    [user_id] int NOT NULL PRIMARY KEY,
                    [display_name] nvarchar(200) NULL,
                    [score] int NULL,
                    [other_score] int NULL,
                    [is_active] bit NOT NULL,
                    [state] int NOT NULL,
                    [external_id] uniqueidentifier NOT NULL,
                    [created] datetime2(7) NOT NULL,
                    [payload] varbinary(100) NULL
                );
                CREATE TABLE [integration].[odd]]table] ([odd]]id] int NOT NULL);
                """);
        }
        catch
        {
            await container.DisposeAsync();
            container = null;
            throw;
        }
    }

    [SetUp]
    public async Task RestoreData()
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            TRUNCATE TABLE [integration].[users];
            TRUNCATE TABLE [integration].[odd]]table];
            INSERT INTO [integration].[odd]]table] ([odd]]id]) VALUES (42);
            """;
        await command.ExecuteNonQueryAsync();

        command.CommandText = """
            INSERT INTO [integration].[users]
                ([user_id], [display_name], [score], [other_score], [is_active], [state], [external_id], [created], [payload])
            VALUES (@id, @name, @score, @otherScore, @active, @state, @externalId, @created, @payload)
            """;
        command.Parameters.Add("@id", SqlDbType.Int);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 200);
        command.Parameters.Add("@score", SqlDbType.Int);
        command.Parameters.Add("@otherScore", SqlDbType.Int);
        command.Parameters.Add("@active", SqlDbType.Bit);
        command.Parameters.Add("@state", SqlDbType.Int);
        command.Parameters.Add("@externalId", SqlDbType.UniqueIdentifier);
        command.Parameters.Add("@created", SqlDbType.DateTime2);
        command.Parameters.Add("@payload", SqlDbType.VarBinary, 100);

        foreach (var user in SeedRows())
        {
            command.Parameters["@id"].Value = user.Id;
            command.Parameters["@name"].Value = (object?)user.Name ?? DBNull.Value;
            command.Parameters["@score"].Value = (object?)user.Score ?? DBNull.Value;
            command.Parameters["@otherScore"].Value = (object?)user.OtherScore ?? DBNull.Value;
            command.Parameters["@active"].Value = user.Active;
            command.Parameters["@state"].Value = (int)user.State;
            command.Parameters["@externalId"].Value = user.ExternalId;
            command.Parameters["@created"].Value = user.Created;
            command.Parameters["@payload"].Value = (object?)user.Payload ?? DBNull.Value;
            await command.ExecuteNonQueryAsync();
        }
    }

    [OneTimeTearDown]
    public async Task StopDatabase()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
            container = null;
        }
    }

    [Test]
    public void Select_ReadsAllRowsAndMappedTypes()
    {
        using var connection = new SqlConnection(connectionString);

        var actual = connection.Select<User>().OrderBy(user => user.Id).ToArray();
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
                Assert.That(row.Payload, Is.EqualTo(seed.Payload));
                Assert.That(row.Unmapped, Is.EqualTo("default"));
            });
        }
    }

    [TestCaseSource(nameof(Predicates))]
    public void Select_WithPredicate_ReturnsMatchingRows(Expression<Func<User, bool>> predicate)
    {
        using var connection = new SqlConnection(connectionString);

        var actual = connection.Select(predicate).Select(user => user.Id);
        var expected = SeedRows().Where(predicate.Compile()).Select(user => user.Id);

        Assert.That(actual, Is.EquivalentTo(expected));
    }

    [Test]
    public void Select_ParameterContainingSql_ReturnsExactMatchAndLeavesTableIntact()
    {
        var name = ParameterValue;
        using var connection = new SqlConnection(connectionString);

        var users = connection.Select<User>(user => user.Name == name);

        Assert.That(users.Select(user => user.Id), Is.EqualTo(new[] { 4 }));
        Assert.That(connection.Select<User>(), Has.Count.EqualTo(5));
    }

    [Test]
    public void Select_NoMatchingRows_ReturnsEmptyList()
    {
        using var connection = new SqlConnection(connectionString);

        Assert.That(connection.Select<User>(user => user.Id > 100), Is.Empty);
    }

    [Test]
    public void Select_EmptyTable_ReturnsEmptyList()
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE [integration].[users]";
        command.ExecuteNonQuery();

        Assert.That(connection.Select<User>(), Is.Empty);
    }

    [Test]
    public void Select_IdentifiersContainingClosingBrackets_AreEscaped()
    {
        using var connection = new SqlConnection(connectionString);

        var rows = connection.Select<EscapedRow>(row => row.Id == 42);

        Assert.That(rows.Select(row => row.Id), Is.EqualTo(new[] { 42 }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Select_ClosedConnection_IsClosedAndCanBeReused(bool filtered)
    {
        using var connection = new SqlConnection(connectionString);

        var users = filtered ? connection.Select<User>(user => user.Id == 1) : connection.Select<User>();

        Assert.That(users, Is.Not.Empty);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        Assert.DoesNotThrow(connection.Open);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Select_OpenConnection_RemainsOpen(bool filtered)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        var users = filtered ? connection.Select<User>(user => user.Id == 1) : connection.Select<User>();

        Assert.That(users, Is.Not.Empty);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Open));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.That(command.ExecuteScalar(), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Select_QueryFailure_PreservesOriginalConnectionState(bool initiallyOpen)
    {
        using var connection = new SqlConnection(connectionString);
        if (initiallyOpen)
        {
            connection.Open();
        }

        var exception = Assert.Throws<SqlException>(() => connection.Select<MissingTableRow>());

        Assert.That(exception!.Number, Is.EqualTo(208));
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    private async Task ExecuteSql(string sql)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static User[] SeedRows()
    {
        string?[] names = ["Alice", "Bob", null, ParameterValue, "Ольга"];
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
            Created = new DateTime(2026, 1, id),
            Payload = id is 2 or 3 ? null : new byte[] { (byte)id, 0, 255 }
        }).ToArray();
    }

    private static IEnumerable<TestCaseData> Predicates()
    {
        var minId = 1;
        int? score = null;
        var externalId = SeedRows()[0].ExternalId;
        var date = new DateTime(2026, 1, 3);
        Expression<Func<User, bool>>[] predicates =
        [
            user => user.Id == 2,
            user => user.Id != 2,
            user => user.Id > 2,
            user => user.Id >= 2,
            user => user.Id < 2,
            user => user.Id <= 2,
            user => user.Id > minId && user.Name != null,
            user => (user.Id < 3 || user.Id == 5) && !user.Active,
            user => user.Active,
            user => user.Name == null,
            user => user.Score == score,
            user => user.Score != 10,
            user => !(user.Score > 10),
            user => user.Score == user.OtherScore,
            user => user.Score != user.OtherScore,
            user => !(user.Score == user.OtherScore),
            user => user.State == UserState.Active,
            user => user.ExternalId == externalId,
            user => user.Created >= date,
            user => user.Name == "Ольга"
        ];
        return predicates.Select(predicate => new TestCaseData(predicate).SetName($"Select SQL: {predicate}"));
    }

    [Table("integration", "users")]
    public class User
    {
        [Column("user_id")]
        public int Id { get; set; }

        [Column("display_name")]
        public string? Name { get; set; }

        [Column("score")]
        public int? Score { get; set; }

        [Column("other_score")]
        public int? OtherScore { get; set; }

        [Column("is_active")]
        public bool Active { get; set; }

        [Column("state")]
        public UserState State { get; set; }

        [Column("external_id")]
        public Guid ExternalId { get; set; }

        [Column("created")]
        public DateTime Created { get; set; }

        [Column("payload")]
        public byte[]? Payload { get; set; }

        public string Unmapped { get; set; } = "default";
    }

    public enum UserState
    {
        Inactive,
        Active
    }

    [Table("integration", "odd]table")]
    public class EscapedRow
    {
        [Column("odd]id")]
        public int Id { get; set; }
    }

    [Table("integration", "missing_table")]
    public class MissingTableRow
    {
        [Column("user_id")]
        public int Id { get; set; }
    }
}
