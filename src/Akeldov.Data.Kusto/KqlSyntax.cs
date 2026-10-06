using System.Text;

namespace Akeldov.Data.Kusto;

internal static class KqlSyntax
{
    internal static string QuoteIdentifier(string name) => $"[{QuoteString(name)}]";

    internal static string QuoteString(string value)
    {
        var result = new StringBuilder("'");
        foreach (var character in value)
        {
            result.Append(character switch
            {
                '\\' => "\\\\",
                '\'' => "\\'",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(character) => $"\\u{(int)character:x4}",
                _ => character.ToString()
            });
        }

        return result.Append('\'').ToString();
    }
}
