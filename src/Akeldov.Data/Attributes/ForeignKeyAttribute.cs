namespace Akeldov.Data;

/// <summary>
/// Marks a property whose value references a property on another model.
/// </summary>
/// <remarks>
/// Describes the relationship as metadata. It does not create a database constraint
/// or load the referenced row. Use Column to map the property to its local column.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ForeignKeyAttribute : Attribute
{
    /// <summary>
    /// Initializes the attribute with the referenced model type and CLR property name.
    /// </summary>
    public ForeignKeyAttribute(Type referencedType, string referencedProperty)
    {
        ArgumentNullException.ThrowIfNull(referencedType);
        ArgumentException.ThrowIfNullOrWhiteSpace(referencedProperty);

        ReferencedType = referencedType;
        ReferencedProperty = referencedProperty;
    }

    /// <summary>
    /// Gets the referenced model type, whose Table attribute identifies the target table.
    /// </summary>
    public Type ReferencedType { get; }

    /// <summary>
    /// Gets the referenced CLR property name, whose Column attribute identifies the target column.
    /// Use nameof to specify this name.
    /// </summary>
    public string ReferencedProperty { get; }
}
