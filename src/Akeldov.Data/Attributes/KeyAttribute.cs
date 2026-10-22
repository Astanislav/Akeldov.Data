namespace Akeldov.Data;

/// <summary>
/// Marks a property as the model's primary key.
/// </summary>
/// <remarks>
/// Use Column to map the property to its database column.
/// This attribute describes metadata; it does not generate key values or create a database constraint.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class KeyAttribute : Attribute
{
}
