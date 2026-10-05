using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using Akeldov.Data.IntegrationTests;
using Akeldov.Data.SqlServer;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using Testcontainers.MsSql;

namespace Akeldov.Data.SqlServer.IntegrationTests;

/// <summary>Requires Docker with Linux containers. The container owns the temporary database.</summary>
[TestFixture]
public class SqlConnectionSelectTests : SelectIntegrationTests
{
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
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

    protected override async Task ResetData()
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


    protected override DbConnection CreateConnection() => new SqlConnection(connectionString);
    protected override string TruncateUsersSql => "TRUNCATE TABLE [integration].[users]";
    protected override List<T> Select<T>(DbConnection connection, Expression<Func<T, bool>>? predicate = null)
    {
        var sqlConnection = (SqlConnection)connection;
        return predicate is null ? sqlConnection.Select<T>() : sqlConnection.Select(predicate);
    }
    protected override void AssertQueryError(DbException exception)
        => Assert.That(((SqlException)exception).Number, Is.EqualTo(208));

    private async Task ExecuteSql(string sql)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

}
