using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace Akeldov.Data.SqlServer.IntegrationTests;

[TestFixture]
[Category("Integration")]
[Category("AzureSql")]
public class AzureSqlConnectionTests
{
    /// <summary>
    /// Optional check against a real Azure SQL database supplied by the test environment.
    /// </summary>
    [Test]
    public async Task Connection_AuthenticatesAndExecutesQuery()
    {
        var connectionString = Environment.GetEnvironmentVariable("AKELDOV_DATA_AZURE_SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore("Set AKELDOV_DATA_AZURE_SQL_CONNECTION_STRING to run the Azure SQL connection check.");
        }

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo(1));
    }
}
