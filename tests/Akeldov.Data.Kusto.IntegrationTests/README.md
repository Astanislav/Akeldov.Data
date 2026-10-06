# Kusto integration tests

These tests execute the public `Select<T>` and `SelectAsync<T>` APIs through
`Microsoft.Azure.Kusto.Data` against the Kusto query engine. Testcontainers
starts an isolated Linux container using
`mcr.microsoft.com/azuredataexplorer/kustainer-linux:stable`, waits for its
management endpoint, creates the test database, and ingests seed data with
synchronous `.set` commands. The fixture disposes the client and container
after the run, including when setup fails.

Prerequisites:

- A running Docker Engine with Linux containers (Docker Desktop on Windows).
- An x64 processor with SSE4.2/AVX2 support.
- At least 2 GB RAM; Microsoft recommends 4 GB or more.
- Access to Microsoft Container Registry for the first image download, which
  is several GB. Subsequent runs use Docker's image cache.

See the official [emulator installation requirements](https://learn.microsoft.com/en-us/azure/data-explorer/kusto-emulator-install).
Starting the container passes `ACCEPT_EULA=Y`, accepting the emulator's
Microsoft Software License Terms. The emulator has no Entra authentication
or managed ingestion service; this suite tests query execution and mapping.

From the repository root:

```powershell
dotnet test tests/Akeldov.Data.Kusto.IntegrationTests/Akeldov.Data.Kusto.IntegrationTests.csproj
```

The fixture uses a random host port and stores its database inside the
temporary container. It does not connect to an Azure cluster. Its tables are
populated once and all test cases read them, so test order does not affect
results. An unavailable Docker Engine is a setup failure, rather than a
silently skipped integration run.

Coverage includes:

- Filtered and unfiltered sync/async selects, with result sets compared to
  independently seeded rows and compiled C# predicates.
- Nullable equality, inequality, column comparisons, and negation.
- Boolean, integer, enum, GUID, UTC date, timespan, and real values.
- Empty tables/results, empty strings, Unicode, and case-sensitive equality.
- Query parameters containing KQL and names colliding with mapped columns.
- Quoted table/column identifiers and case-distinct column names.
- `DateTimeOffset` mapping, captured values, and invariant parameter formatting.
- Server errors, unsupported expressions, cancellation, and subsequent client reuse.

To run only local unit tests across the solution:

```powershell
dotnet test Akeldov.Data.sln --filter "TestCategory!=Integration"
```
