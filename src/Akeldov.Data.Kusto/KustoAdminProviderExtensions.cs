using System.Globalization;
using System.Text.Json;
using Kusto.Data.Common;

namespace Akeldov.Data.Kusto;

/// <summary>Inserts mapped models using a caller-owned Kusto administration client.</summary>
public static class KustoAdminProviderExtensions
{
    /// <summary>Inserts a row and its Reference entities using inline JSON ingestion and explicit column mappings.</summary>
    /// <remarks>References are inserted first and their caller-supplied Key values are copied to local ForeignKey properties.
    /// Commands are not transactional: earlier inserts remain on failure, but changed foreign keys in memory are restored.
    /// Uses inline ingestion intended for exploration and prototyping. Kusto strings represent null as an empty string.</remarks>
    public static void Create<T>(this ICslAdminProvider provider, T entity) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(entity);
        var builder = new CommandBuilder();
        using var graph = new CreateGraph(StringComparer.Ordinal, builder);
        graph.Prepare(entity);
        foreach (var (database, command) in builder.Commands)
        {
            using var reader = provider.ExecuteControlCommand(database, command, new ClientRequestProperties());
            while (reader.Read()) { }
        }
        graph.Complete();
    }

    /// <summary>Asynchronously inserts a row and its Reference entities, dependencies first.</summary>
    /// <remarks>The SDK does not accept a cancellation token for control commands. Cancellation is checked before each command.
    /// Commands are not transactional: earlier inserts remain on failure or cancellation, but changed foreign keys in memory are restored.</remarks>
    public static async Task CreateAsync<T>(this ICslAdminProvider provider, T entity,
        CancellationToken cancellationToken = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new CommandBuilder();
        using var graph = new CreateGraph(StringComparer.Ordinal, builder);
        graph.Prepare(entity);
        foreach (var (database, command) in builder.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = await provider.ExecuteControlCommandAsync(database, command, new ClientRequestProperties())
                .ConfigureAwait(false);
            while (reader.Read()) { }
        }
        graph.Complete();
    }

    private sealed class CommandBuilder : ICreateGraphCommandBuilder
    {
        public List<(string Database, string Command)> Commands { get; } = [];

        public void Add<T>(T entity) where T : class, new() => Commands.Add(CreateCommand(entity));
    }

    internal static (string Database, string Command) CreateCommand<T>(T entity) where T : class, new()
    {
        var mapping = TableMapping<T>.Create();
        mapping.ValidateGetters();
        var columns = mapping.Columns.ToArray();
        var data = new Dictionary<string, object?>();
        for (var index = 0; index < columns.Length; index++)
        {
            KqlParameter.GetScalarType(columns[index].Property.PropertyType);
            var value = columns[index].Property.GetValue(entity);
            if (value is not null)
            {
                // Reuse the supported scalar types and range checks from Select.
                KqlParameter.Create(index, value);
                if (value.GetType().IsEnum)
                    value = Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture);
                value = value switch
                {
                    DateTime date => date.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime(),
                    DateTimeOffset date => date.UtcDateTime,
                    TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
                    _ => value
                };
            }
            data.Add($"p{index}", value);
        }

        // Synthetic JSON keys avoid interpreting column names as JSON paths.
        var ingestionMapping = JsonSerializer.Serialize(columns.Select((column, index) => new
        {
            column = column.Name,
            Properties = new { Path = $"$.p{index}" }
        }));
        var command = $".ingest inline into table {KqlSyntax.QuoteIdentifier(mapping.Table.Name)} with (format='json', ingestionMapping={KqlSyntax.QuoteString(ingestionMapping)}) <|\n{JsonSerializer.Serialize(data)}";
        return (mapping.Table.Schema, command);
    }
}
