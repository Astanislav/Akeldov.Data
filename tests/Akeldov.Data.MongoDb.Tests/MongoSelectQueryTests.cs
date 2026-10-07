using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using NUnit.Framework;

namespace Akeldov.Data.MongoDb.Tests;

[TestFixture]
public class MongoSelectQueryTests
{
    [Test]
    public void Query_UsesDatabaseCollectionAndOnlyMappedFields()
    {
        var query = new MongoSelectQuery<User>();
        Assert.Multiple(() =>
        {
            Assert.That(query.Database, Is.EqualTo("analytics"));
            Assert.That(query.Collection, Is.EqualTo("users"));
            Assert.That(query.Filter, Is.EqualTo(new BsonDocument()));
            Assert.That(query.Projection, Is.EqualTo(BsonDocument.Parse("{_id: 1, name: 1, score: 1, active: 1}")));
            Assert.That(new MongoSelectQuery<NoId>().Projection, Is.EqualTo(BsonDocument.Parse("{name: 1, _id: 0}")));
        });
    }

    [Test]
    public void Filter_UsesMappedNamesAndCapturedValues()
    {
        var minimum = 10;
        var name = "{$ne: null}";
        var query = new MongoSelectQuery<User>(row => row.Score >= minimum && row.Name == name && row.Active);
        Assert.That(query.Filter, Is.EqualTo(new BsonDocument
        {
            { "score", new BsonDocument("$gte", minimum) }, { "name", name }, { "active", true }
        }));
    }

    [Test]
    public void Filter_BooleanNegationAndNullChecks_AreTranslated()
    {
        Assert.That(new MongoSelectQuery<User>(row => !row.Active).Filter,
            Is.EqualTo(new BsonDocument("active", new BsonDocument("$ne", true))));
        Assert.That(new MongoSelectQuery<User>(row => row.Score == null).Filter,
            Is.EqualTo(new BsonDocument("score", BsonNull.Value)));
        Assert.That(new MongoSelectQuery<User>(row => row.Score != null).Filter,
            Is.EqualTo(new BsonDocument("score", new BsonDocument("$ne", BsonNull.Value))));
    }

    [Test]
    public void GuidKey_UsesStandardUuidForFilteringAndReading()
    {
        var id = Guid.NewGuid();
        var query = new MongoSelectQuery<GuidRow>(row => row.Id == id);
        var binary = new BsonBinaryData(id, GuidRepresentation.Standard);
        Assert.Multiple(() =>
        {
            Assert.That(query.Filter, Is.EqualTo(new BsonDocument("_id", binary)));
            Assert.That(query.Read(new BsonDocument { { "_id", binary }, { "optional", binary } }).Id, Is.EqualTo(id));
            Assert.That(query.Read(new BsonDocument { { "_id", binary }, { "optional", binary } }).Optional, Is.EqualTo(id));
            Assert.That(new MongoSelectQuery<GuidRow>(row => row.Optional == id).Filter,
                Is.EqualTo(new BsonDocument("optional", binary)));
            Assert.That(query.Read(new BsonDocument { { "_id", binary }, { "optional", BsonNull.Value } }).Optional, Is.Null);
        });
    }

    [Test]
    public void ObjectIdKey_IsPreservedForFilteringAndReading()
    {
        var id = ObjectId.GenerateNewId();
        var query = new MongoSelectQuery<User>(row => row.Id == id);
        Assert.That(query.Filter, Is.EqualTo(new BsonDocument("_id", id)));
        Assert.That(query.Read(new BsonDocument("_id", id)).Id, Is.EqualTo(id));
    }

    [Test]
    public void StringAndNumericKeys_AreNotConvertedToGuid()
    {
        var text = new MongoSelectQuery<StringRow>(row => row.Id == "abc");
        var number = new MongoSelectQuery<NumberRow>(row => row.Id == 42);
        Assert.Multiple(() =>
        {
            Assert.That(text.Filter, Is.EqualTo(new BsonDocument("_id", "abc")));
            Assert.That(text.Read(new BsonDocument("_id", "abc")).Id, Is.EqualTo("abc"));
            Assert.That(number.Filter, Is.EqualTo(new BsonDocument("_id", 42)));
            Assert.That(number.Read(new BsonDocument("_id", 42)).Id, Is.EqualTo(42));
        });
    }

    [Test]
    public void Read_NullMissingAndExtraFields_PreserveDefaultsAndUnmappedProperties()
    {
        var row = new MongoSelectQuery<User>().Read(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "name", BsonNull.Value },
            { "extra", 99 }, { "Unmapped", "changed" }
        });
        Assert.Multiple(() =>
        {
            Assert.That(row.Name, Is.Null);
            Assert.That(row.Score, Is.Null);
            Assert.That(row.Active, Is.True);
            Assert.That(row.Unmapped, Is.EqualTo("default"));
        });
    }

    [Test]
    public void Read_NullNonNullableProperty_Throws()
    {
        Assert.That(() => new MongoSelectQuery<User>().Read(new BsonDocument("active", BsonNull.Value)),
            Throws.InvalidOperationException.With.Message.Contains("not nullable"));
    }

    [Test]
    public void Read_Scalars_UseNativeBsonSerializers()
    {
        var date = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        var row = new MongoSelectQuery<ScalarRow>().Read(new BsonDocument
        {
            { "created", new BsonDateTime(date) }, { "amount", new BsonDecimal128(1.5m) },
            { "payload", new BsonBinaryData(new byte[] { 1, 2 }) }, { "state", 1 }
        });
        Assert.Multiple(() =>
        {
            Assert.That(row.Created, Is.EqualTo(date));
            Assert.That(row.Created.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(row.Amount, Is.EqualTo(1.5m));
            Assert.That(row.Payload, Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(row.State, Is.EqualTo(State.Active));
        });
    }

    [Test]
    public void Mapping_PreservesCaseAndInheritedProperties()
    {
        var query = new MongoSelectQuery<DerivedRow>(row => row.Id == 1 && row.OtherId == 2);
        var row = query.Read(new BsonDocument { { "id", 1 }, { "ID", 2 } });
        Assert.That(query.Filter, Is.EqualTo(new BsonDocument { { "id", 1 }, { "ID", 2 } }));
        Assert.That(row.Id, Is.EqualTo(1));
        Assert.That(row.OtherId, Is.EqualTo(2));
    }

    [Test]
    public void Mapping_DoesNotChangeGlobalDriverMapping()
    {
        var global = BsonSerializer.LookupSerializer<NoId>();
        var info = (IBsonDocumentSerializer)global;
        info.TryGetMemberSerializationInfo(nameof(NoId.Name), out var before);
        var query = new MongoSelectQuery<NoId>(row => row.Name == "Alice");
        info.TryGetMemberSerializationInfo(nameof(NoId.Name), out var after);
        Assert.Multiple(() =>
        {
            Assert.That(before.ElementName, Is.EqualTo("driver_name"));
            Assert.That(after.ElementName, Is.EqualTo(before.ElementName));
            Assert.That(query.Filter, Is.EqualTo(new BsonDocument("name", "Alice")));
        });
    }

    [Test]
    public void Mapping_MissingAttributesOrDuplicateFields_Throws()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new MongoSelectQuery<Unmapped>(), Throws.InvalidOperationException);
            Assert.That(() => new MongoSelectQuery<NoColumns>(), Throws.InvalidOperationException);
            Assert.That(() => new MongoSelectQuery<Duplicate>(), Throws.InvalidOperationException);
        });
    }

    [Test]
    public void Mapping_DottedField_IsRejected()
        => Assert.That(() => new MongoSelectQuery<Dotted>(), Throws.TypeOf<NotSupportedException>());

    [Test]
    public void Filter_UnmappedPropertyOrUnsupportedMethod_Throws()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new MongoSelectQuery<User>(row => row.Unmapped == "value"), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => new MongoSelectQuery<User>(row => Unsupported(row.Name)), Throws.InstanceOf<NotSupportedException>());
        });
    }

    internal static bool Unsupported(string? value) => value == "custom";

    [Table("analytics", "users")]
    public class User
    {
        [Column("_id")] public ObjectId Id { get; set; }
        [Column("name")] public string? Name { get; set; }
        [Column("score")] public int? Score { get; set; }
        [Column("active")] public bool Active { get; set; } = true;
        public string Unmapped { get; set; } = "default";
    }

    [Table("analytics", "guids")]
    public class GuidRow
    {
        [Column("_id")] public Guid Id { get; set; }
        [Column("optional")] public Guid? Optional { get; set; }
    }

    [Table("analytics", "strings")]
    public class StringRow { [Column("_id")] public string Id { get; set; } = ""; }
    [Table("analytics", "numbers")]
    public class NumberRow { [Column("_id")] public int Id { get; set; } }
    [Table("analytics", "no_id")]
    public class NoId { [Column("name"), BsonElement("driver_name")] public string? Name { get; set; } }
    public class Unmapped { }
    [Table("analytics", "none")]
    public class NoColumns { public int Id { get; set; } }
    [Table("analytics", "duplicates")]
    public class Duplicate
    {
        [Column("same")] public int First { get; set; }
        [Column("same")] public int Second { get; set; }
    }
    [Table("analytics", "dotted")]
    public class Dotted { [Column("nested.name")] public string? Name { get; set; } }
    public class BaseRow { [Column("id")] public int Id { get; set; } }
    [Table("analytics", "case")]
    public class DerivedRow : BaseRow { [Column("ID")] public int OtherId { get; set; } }
    public enum State { Inactive, Active }
    [Table("analytics", "scalars")]
    public class ScalarRow
    {
        [Column("created")] public DateTime Created { get; set; }
        [Column("amount")] public decimal Amount { get; set; }
        [Column("payload")] public byte[]? Payload { get; set; }
        [Column("state")] public State State { get; set; }
    }
}
