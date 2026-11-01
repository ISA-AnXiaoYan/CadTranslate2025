using System.Text.Json;

namespace CadTranslate2025;

public sealed class TranslationMemory
{
    private readonly string _path;
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    private TranslationMemory(string path) => _path = path;

    public static TranslationMemory Load(string path)
    {
        var memory = new TranslationMemory(path);
        if (!File.Exists(path)) return memory;
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (data != null) foreach (var pair in data) memory._entries[pair.Key] = pair.Value;
        }
        catch { /* corrupted cache is recoverable; translation continues */ }
        return memory;
    }

    public bool TryGet(string key, out string value) => _entries.TryGetValue(key, out value!);

    public void Set(string key, string value)
    {
        _entries[key] = value;
        Save();
    }

    public void SetMany(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var pair in entries) _entries[pair.Key] = pair.Value;
        Save();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, true);
    }
}

