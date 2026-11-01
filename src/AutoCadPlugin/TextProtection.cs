using System.Text.RegularExpressions;

namespace CadTranslate2025;

internal static class TextProtection
{
    private static readonly Regex ProtectedToken = new(@"(?<![A-Za-z])%%\d+|(?<![A-Za-z])(?:MD\s*\d{1,2}[./]\d{1,2}[./]\d{2,4}|[A-Z]{1,4}\d{2,}[A-Z]?|\d+(?:[.,:/-]\d+)*(?:\s*(?:cm|mm|m|m²|㎡|%))?)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Protect(string source, out Dictionary<string, string> values)
    {
        var protectedValues = new Dictionary<string, string>();
        values = protectedValues;
        var index = 0;
        return ProtectedToken.Replace(source, match =>
        {
            var token = $"__CADP{index++}__";
            protectedValues[token] = match.Value;
            return token;
        });
    }

    public static bool TryRestore(string translated, string source, out string restored)
    {
        _ = Protect(source, out var values);
        if (values.Keys.Any(token => !translated.Contains(token, StringComparison.Ordinal)))
        {
            restored = translated;
            return false;
        }
        restored = translated;
        foreach (var pair in values) restored = restored.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return true;
    }
}

