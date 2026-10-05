using System.Data.Common;
using System.Linq.Expressions;
using Akeldov.Data.IntegrationTests;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;
using Testcontainers.PostgreSql;

namespace Akeldov.Data.PostgreSql.IntegrationTests;

/// <summary>Requires Docker with Linux containers. The container owns the temporary database.</summary>
[TestFixture]
public class NpgsqlConnectionSelectTests : SelectIntegrationTests
{
    private PostgreSqlContainer? container;
    private string connectionString = null!;

    [OneTimeSetUp]
    public async Task StartDatabase()
    {
        container = new PostgreSqlBuilder("postgres:17.6-alpine")
            .WithDatabase($"akeldov_data_tests_{Guid.NewGuid():N}")
            .Build();
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await container.StartAsync(cancellation.Token);
            connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                Pooling = false
            }.ConnectionString;
            await ExecuteSql("""
                CREATE SCHEMA "integration";
                CREATE TABLE "integration"."users"
                (
                    "user_id" integer NOT NULL PRIMARY KEY,
                    "display_name" text NULL,
                    "score" integer NULL,
                    "other_score" integer NULL,
                    "is_active" boolean NOT NULL,
                    "state" integer NOT NULL,
                    "external_id" uuid NOT NULL,
                    "created" timestamp without time zone NOT NULL,
                    "payload" bytea NULL
                );
                CREATE TABLE "integration"."odd]table" ("odd]id" integer NOT NULL);
                CREATE TABLE "integration"."CaseRows"
                    ("id" integer NOT NULL, "ID" integer NOT NULL, "text""column" text NOT NULL);
                CREATE TABLE "integration"."utc_rows" ("created" timestamp with time zone NOT NULL);
                """);
        }
        catch
        {
            await container.DisposeAsync();
            container = null;
            throw;
        }
    }

    protected override async Task ResetData()
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            TRUNCATE TABLE "integration"."users", "integration"."odd]table",
                "integration"."CaseRows", "integration"."utc_rows";
            INSERT INTO "integration"."odd]table" ("odd]id") VALUES (42);
            INSERT INTO "integration"."CaseRows" ("id", "ID", "text""column") VALUES (1, 2, 'quoted');
            INSERT INTO "integration"."utc_rows" ("created") VALUES (TIMESTAMPTZ '2026-01-01 00:00:00+00');
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = """
            INSERT INTO "integration"."users"
                ("user_id", "display_name", "score", "other_score", "is_active", "state", "external_id", "created", "payload")
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """;
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Timestamp });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea });
        foreach (var user in SeedRows())
        {
            command.Parameters[0].Value = user.Id;
            command.Parameters[1].Value = (object?)user.Name ?? DBNull.Value;
            command.Parameters[2].Value = (object?)user.Score ?? DBNull.Value;
            command.Parameters[3].Value = (object?)user.OtherScore ?? DBNull.Value;
            command.Parameters[4].Value = user.Active;
            command.Parameters[5].Value = (int)user.State;
            command.Parameters[6].Value = user.ExternalId;
            command.Parameters[7].Value = user.Created;
            command.Parameters[8].Value = (object?)user.Payload ?? DBNull.Value;
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
    public void Select_QuotedIdentifiers_PreserveCaseAndEscapeDoubleQuotes()
    {
        using var connection = new NpgsqlConnection(connectionString);
        var rows = connection.Select<CaseRow>(row => row.Id == 1 && row.AlternateId == 2 && row.Name == "quoted");

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].AlternateId, Is.EqualTo(2));
        Assert.That(rows[0].Name, Is.EqualTo("quoted"));
    }

    [Test]
    public void Select_UtcDateParameter_ReadsTimestampWithTimeZone()
    {
        var threshold = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var connection = new NpgsqlConnection(connectionString);
        var rows = connection.Select<UtcRow>(row => row.Created >= threshold);

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Created, Is.EqualTo(threshold));
        Assert.That(rows[0].Created.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void Select_ConnectionFromDataSource_UsesConfiguredConnection()
    {
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        using var connection = dataSource.OpenConnection();
        var rows = connection.Select<User>(row => row.Id == 1);

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(connection.State, Is.EqualTo(System.Data.ConnectionState.Open));
    }

    protected override DbConnection CreateConnection() => new NpgsqlConnection(connectionString);
    protected override string TruncateUsersSql => """TRUNCATE TABLE "integration"."users" """;
    protected override List<T> Select<T>(DbConnection connection, Expression<Func<T, bool>>? predicate = null)
    {
        var pgConnection = (NpgsqlConnection)connection;
        return predicate is null ? pgConnection.Select<T>() : pgConnection.Select(predicate);
    }
    protected override void AssertQueryError(DbException exception)
        => Assert.That(((PostgresException)exception).SqlState, Is.EqualTo(PostgresErrorCodes.UndefinedTable));

    private async Task ExecuteSql(string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Table("integration", "CaseRows")]
    public class CaseRow
    {
        [Column("id")]
        public int Id { get; set; }
        [Column("ID")]
        public int AlternateId { get; set; }
        [Column("text\"column")]
        public string? Name { get; set; }
    }

    [Table("integration", "utc_rows")]
    public class UtcRow
    {
        [Column("created")]
        public DateTime Created { get; set; }
    }
}
