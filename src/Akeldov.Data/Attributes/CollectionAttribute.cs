namespace Akeldov.Data;

/// <summary>Describes a collection of child entities linked by a foreign key on the child model.</summary>
/// <remarks>Metadata only: does not load, insert, update or delete children automatically.</remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class CollectionAttribute : Attribute
{
    /// <param name="foreignKeyProperty">CLR name of the child property marked with Column and ForeignKey.</param>
    public CollectionAttribute(string foreignKeyProperty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignKeyProperty);
        ForeignKeyProperty = foreignKeyProperty;
    }

    public string ForeignKeyProperty { get; }
}
