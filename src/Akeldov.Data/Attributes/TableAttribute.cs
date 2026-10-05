namespace Akeldov.Data;

/// <summary>
/// Marks a class as a model of a row in the specified database table.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TableAttribute : Attribute
{
    /// <summary>
    /// Initializes the attribute with the table's schema and name.
    /// </summary>
    public TableAttribute(string schema, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Schema = schema;
        Name = name;
    }

    /// <summary>
    /// Gets the database schema containing the table.
    /// </summary>
    public string Schema { get; }

    /// <summary>
    /// Gets the table name.
    /// </summary>
    public string Name { get; }
}
