using System.Text.RegularExpressions;

public static class TextRules
{
    // Identifier syntax is ASCII. Unicode \w includes Arabic room names.
    private static readonly Regex Code = new(@"^[A-Z]{0,7}[-_]?\d[A-Za-z0-9_./:+ -]*$", RegexOptions.CultureInvariant);
    private static readonly Regex RoomPrefix = new(@"^(?:\d+[-_ ]*)?(?:office|patient[-_ ]*room|hospitalzation[-_ ]*room|room|wc|waiting|corridor|clinic|clinc|ward|toilet|sound)(?=$|[-_/ ]|\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Protected = new(@"(?:[A-Za-z]+[A-Za-z0-9_-]*\d[A-Za-z0-9_./:+-]*|\d+(?:[.,:/+-]\d+)*(?:\s*(?:mm²|m²|mm2|m2|mm|cm|kg|kW|KW|kVA|kva|kV|Hz|hz|l/s|L/S|m3/h|m³/h|in|m|%))?|[Ø⌀])", RegexOptions.CultureInvariant);
    public static bool IsRoomLabel(string value) => RoomPrefix.IsMatch(value.Trim());
    public static bool IsIdentifier(string value) => Code.IsMatch(value.Trim()) && !IsRoomLabel(value);
    public static string Mask(string source, List<string> values)
    {
        string Keep(string value) { string token=$"__KEEP{values.Count}__"; values.Add(value); return token; }
        return Protected.Replace(source, m =>
        {
            if (!IsRoomLabel(m.Value)) return Keep(m.Value);
            // Translate the room name, protect its number, and retain equipment suffixes.
            int slash=m.Value.IndexOf('/');
            string label=slash<0?m.Value:m.Value[..slash];
            string masked=Regex.Replace(label,@"\d+(?:[.,:]\d+)*", n=>Keep(n.Value));
            return slash<0?masked:masked+Keep(m.Value[slash..]);
        });
    }
}
