namespace Akeldov.Data;

/// <summary>
/// Marks a single entity navigation or a child collection (List&lt;T&gt; or T[]).
/// </summary>
/// <remarks>
/// SQL Server, PostgreSQL and Kusto Create save a non-null referenced entity first and copy its Key to the local ForeignKey.
/// A null reference leaves the local foreign key value unchanged. Other providers treat this as metadata.
/// SQL Server Select loads direct child collections. Single entity references are not automatically loaded.
/// Collection references are not automatically saved by Create.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ReferenceAttribute : Attribute
{
    /// <summary>
    /// Names the local ForeignKey for a single entity, or the child's ForeignKey for a collection.
    /// </summary>
    public ReferenceAttribute(string foreignKeyProperty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignKeyProperty);

        ForeignKeyProperty = foreignKeyProperty;
    }

    /// <summary>
    /// Gets the foreign key property's CLR name. Use nameof to specify this name.
    /// </summary>
    public string ForeignKeyProperty { get; }
}
