using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using NUnit.Framework;

namespace Akeldov.Data.IntegrationTests;

/// <summary>Shared contract tests executed against each database provider.</summary>
[Category("Integration")]
[NonParallelizable]
public abstract class SelectIntegrationTests
{
    protected const string ParameterValue = "Robert'); DROP TABLE [integration].[users];--";
    protected abstract DbConnection CreateConnection();
    protected abstract List<T> Select<T>(DbConnection connection, Expression<Func<T, bool>>? predicate = null)
        where T : class, new();
    protected abstract Task ResetData();
    protected abstract string TruncateUsersSql { get; }
    protected abstract void AssertQueryError(DbException exception);

    [SetUp]
    public Task RestoreData() => ResetData();

    [Test]
    public void Select_ReadsAllRowsAndMappedTypes()
    {
        using var connection = CreateConnection();

        var actual = Select<User>(connection).OrderBy(user => user.Id).ToArray();
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
        using var connection = CreateConnection();

        var actual = Select(connection, predicate).Select(user => user.Id);
        var expected = SeedRows().Where(predicate.Compile()).Select(user => user.Id);

        Assert.That(actual, Is.EquivalentTo(expected));
    }

    [Test]
    public void Select_ParameterContainingSql_ReturnsExactMatchAndLeavesTableIntact()
    {
        var name = ParameterValue;
        using var connection = CreateConnection();

        var users = Select<User>(connection, user => user.Name == name);

        Assert.That(users.Select(user => user.Id), Is.EqualTo(new[] { 4 }));
        Assert.That(Select<User>(connection), Has.Count.EqualTo(5));
    }

    [Test]
    public void Select_NoMatchingRows_ReturnsEmptyList()
    {
        using var connection = CreateConnection();

        Assert.That(Select<User>(connection, user => user.Id > 100), Is.Empty);
    }

    [Test]
    public void Select_EmptyTable_ReturnsEmptyList()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = TruncateUsersSql;
        command.ExecuteNonQuery();

        Assert.That(Select<User>(connection), Is.Empty);
    }

    [Test]
    public void Select_IdentifiersContainingClosingBrackets_AreEscaped()
    {
        using var connection = CreateConnection();

        var rows = Select<EscapedRow>(connection, row => row.Id == 42);

        Assert.That(rows.Select(row => row.Id), Is.EqualTo(new[] { 42 }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Select_ClosedConnection_IsClosedAndCanBeReused(bool filtered)
    {
        using var connection = CreateConnection();

        var users = filtered ? Select<User>(connection, user => user.Id == 1) : Select<User>(connection);

        Assert.That(users, Is.Not.Empty);
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        Assert.DoesNotThrow(connection.Open);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Select_OpenConnection_RemainsOpen(bool filtered)
    {
        using var connection = CreateConnection();
        connection.Open();

        var users = filtered ? Select<User>(connection, user => user.Id == 1) : Select<User>(connection);

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
        using var connection = CreateConnection();
        if (initiallyOpen)
        {
            connection.Open();
        }

        var exception = Assert.Catch<DbException>(() => Select<MissingTableRow>(connection));

        AssertQueryError(exception!);
        Assert.That(connection.State, Is.EqualTo(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed));
    }

    protected static User[] SeedRows()
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

    protected static IEnumerable<TestCaseData> Predicates()
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
