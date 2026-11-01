using System.Text;

namespace CadTranslate2025;

public sealed class Glossary
{
    private readonly List<GlossaryTerm> _terms;

    private Glossary(List<GlossaryTerm> terms) => _terms = terms;

    public IReadOnlyList<GlossaryTerm> Terms => _terms;

    public static Glossary Load(string path)
    {
        if (!File.Exists(path)) return new Glossary([]);
        var rows = File.ReadAllLines(path, Encoding.UTF8)
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.TrimStart().StartsWith("#"))
            .Select(ParseCsvLine)
            .ToList();
        if (rows.Count == 0) return new Glossary([]);

        var header = rows[0].Select((value, index) => new { value = value.Trim().ToLowerInvariant(), index })
            .ToDictionary(x => x.value, x => x.index);
        string Get(string[] row, string key) => header.TryGetValue(key, out var i) && i < row.Length ? row[i].Trim() : "";
        var terms = rows.Skip(1)
            .Select(row => new GlossaryTerm(
                Get(row, "zh"), Get(row, "en"), Get(row, "aliases").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Get(row, "match_mode"), Get(row, "locked").Equals("true", StringComparison.OrdinalIgnoreCase), Get(row, "project"), Get(row, "notes")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Zh) && !string.IsNullOrWhiteSpace(x.En))
            .ToList();
        return new Glossary(terms);
    }

    public IReadOnlyList<GlossaryTerm> Relevant(string source)
    {
        return _terms.Where(t =>
            source.Contains(t.Zh, StringComparison.OrdinalIgnoreCase) ||
            source.Contains(t.En, StringComparison.OrdinalIgnoreCase) ||
            t.Aliases.Any(a => source.Contains(a, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => Math.Max(t.Zh.Length, t.En.Length))
            .Take(40)
            .ToList();
    }

    public string ApplyLocked(string source, TranslationDirection direction)
    {
        var result = source;
        foreach (var term in _terms.Where(t => t.Locked).OrderByDescending(t => Math.Max(t.Zh.Length, t.En.Length)))
        {
            var from = direction == TranslationDirection.ChineseToEnglish ? term.Zh : term.En;
            var to = direction == TranslationDirection.ChineseToEnglish ? term.En : term.Zh;
            foreach (var candidate in new[] { from }.Concat(term.Aliases))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                result = result.Replace(candidate, to, StringComparison.OrdinalIgnoreCase);
            }
        }
        return result;
    }

    public bool TryExact(string source, TranslationDirection direction, out string target)
    {
        foreach (var term in _terms)
        {
            var from = direction == TranslationDirection.ChineseToEnglish ? term.Zh : term.En;
            var to = direction == TranslationDirection.ChineseToEnglish ? term.En : term.Zh;
            if (source.Trim().Equals(from.Trim(), StringComparison.OrdinalIgnoreCase) ||
                term.Aliases.Any(a => source.Trim().Equals(a.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                target = to;
                return true;
            }
        }
        target = source;
        return false;
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result.ToArray();
    }
}

