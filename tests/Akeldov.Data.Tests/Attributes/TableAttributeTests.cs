using System.Reflection;
using NUnit.Framework;

namespace Akeldov.Data.Tests;

[TestFixture]
public class TableAttributeTests
{
    [Test]
    public void AnnotatedClass_ExposesSchemaAndTableName()
    {
        var attribute = typeof(User).GetCustomAttribute<TableAttribute>();

        Assert.That(attribute, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(attribute!.Schema, Is.EqualTo("dbo"));
            Assert.That(attribute.Name, Is.EqualTo("users"));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("\t\r\n")]
    public void Constructor_InvalidSchema_ThrowsArgumentException(string? schema)
    {
        var exception = Assert.Catch<ArgumentException>(() => new TableAttribute(schema!, "users"));

        Assert.That(exception!.ParamName, Is.EqualTo("schema"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("\t\r\n")]
    public void Constructor_InvalidName_ThrowsArgumentException(string? name)
    {
        var exception = Assert.Catch<ArgumentException>(() => new TableAttribute("dbo", name!));

        Assert.That(exception!.ParamName, Is.EqualTo("name"));
    }

    [Table("dbo", "users")]
    private class User
    {
    }
}
