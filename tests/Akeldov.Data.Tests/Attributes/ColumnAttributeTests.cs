using System.Reflection;
using NUnit.Framework;

namespace Akeldov.Data.Tests;

[TestFixture]
public class ColumnAttributeTests
{
    [Test]
    public void AnnotatedProperty_ExposesColumnName()
    {
        var property = typeof(User).GetProperty(nameof(User.Id));
        var attribute = property!.GetCustomAttribute<ColumnAttribute>();

        Assert.That(attribute, Is.Not.Null);
        Assert.That(attribute!.Name, Is.EqualTo("user_id"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("\t\r\n")]
    public void Constructor_InvalidName_ThrowsArgumentException(string? name)
    {
        var exception = Assert.Catch<ArgumentException>(() => new ColumnAttribute(name!));

        Assert.That(exception!.ParamName, Is.EqualTo("name"));
    }

    private class User
    {
        [Column("user_id")]
        public int Id { get; set; }
    }
}
