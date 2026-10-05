namespace Akeldov.Data;

/// <summary>
/// Marks a property as a model of the specified database table column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ColumnAttribute : Attribute
{
    /// <summary>
    /// Initializes the attribute with the column name.
    /// </summary>
    public ColumnAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
    }

    /// <summary>
    /// Gets the column name.
    /// </summary>
    public string Name { get; }
}
