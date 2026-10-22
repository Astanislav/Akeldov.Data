namespace Akeldov.Data;

/// <summary>
/// Marks a navigation property for an entity referenced by a foreign key on the same model.
/// </summary>
/// <remarks>
/// Describes the relationship as metadata. It does not automatically load or save the referenced entity.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ReferenceAttribute : Attribute
{
    /// <summary>
    /// Initializes the attribute with the CLR name of the local property marked with ForeignKey.
    /// </summary>
    public ReferenceAttribute(string foreignKeyProperty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignKeyProperty);

        ForeignKeyProperty = foreignKeyProperty;
    }

    /// <summary>
    /// Gets the local foreign key property's CLR name. Use nameof to specify this name.
    /// </summary>
    public string ForeignKeyProperty { get; }
}
