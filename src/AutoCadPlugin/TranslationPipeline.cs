using Autodesk.AutoCAD.DatabaseServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CadTranslate2025;

public static class TranslationPipeline
{
    private static readonly Regex HasLetter = new(@"\p{L}", RegexOptions.Compiled);

    public static PipelineSummary Run(
        string sourcePath,
        string outputPath,
        TranslationDirection direction,
        string glossaryPath,
        string cachePath,
        Action<string>? progress = null,
        bool forceLarge = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var logPath = Path.ChangeExtension(outputPath, ".translation.log.jsonl");
        using var database = new Database(false, true);
        database.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, false, null);

        var items = AutoCadTextRepository.Scan(database);
        var candidates = items.Where(x => HasLetter.IsMatch(TextUtil.ToPlainText(x.Source))).ToList();
        progress?.Invoke($"扫描 {items.Count} 个文字对象，候选 {candidates.Count} 个");

        if (forceLarge || candidates.Count > LargePipeline.LargeThreshold)
        {
            return LargePipeline.Start(sourcePath, outputPath, direction, glossaryPath, cachePath,
                items.Count, candidates, progress);
        }

        var glossary = Glossary.Load(glossaryPath);
        var cache = TranslationMemory.Load(cachePath);
        using var log = new StreamWriter(logPath, false, System.Text.Encoding.UTF8);
        WriteLog(log, new { event_name = "scan", source = sourcePath, scanned = items.Count, candidates = candidates.Count, direction = direction.ToString() });

        var byHandle = new Dictionary<string, TranslationResult>(StringComparer.OrdinalIgnoreCase);
        var pending = new Dictionary<string, TranslationRequest>(StringComparer.Ordinal);
        var pendingSources = new Dictionary<string, string>(StringComparer.Ordinal);
        var protectionById = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var itemsBySource = candidates
            .GroupBy(x => TextUtil.Normalize(x.Source), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var immediate = 0;
        foreach (var item in candidates)
        {
            var plain = TextUtil.Normalize(item.Source);
            var cacheKey = CacheKey(direction, glossaryPath, plain);
            if (glossary.TryExact(plain, direction, out var exact))
            {
                byHandle[item.Handle] = new TranslationResult(item.Handle, item.Source, exact, "glossary", null, true, false);
                immediate++;
                continue;
            }
            if (cache.TryGet(cacheKey, out var cached))
            {
                var target = glossary.ApplyLocked(cached, direction);
                byHandle[item.Handle] = new TranslationResult(item.Handle, item.Source, target, "cache", null, false, true);
                immediate++;
                continue;
            }
            var id = TextUtil.Sha256(direction + "|" + plain)[..16];
            if (!pending.ContainsKey(id))
            {
                var protectedText = TextProtection.Protect(plain, out var protections);
                pending[id] = new TranslationRequest(id, plain, protectedText, direction, glossary.Relevant(plain));
                pendingSources[id] = cacheKey;
                protectionById[id] = protections;
            }
        }

        using var client = pending.Count > 0 ? new DeepSeekClient() : null;
        var translated = 0;
        var cacheWrites = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var batch in pending.Values.Chunk(80))
        {
            progress?.Invoke($"调用 DeepSeek 翻译 {batch.Length} 条唯一文本");
            var response = client!.TranslateAsync(batch).GetAwaiter().GetResult();
            foreach (var result in response)
            {
                var request = pending[result.Id];
                var protections = protectionById[result.Id];
                if (protections.Keys.Any(token => !result.Target.Contains(token, StringComparison.Ordinal)))
                {
                    WriteLog(log, new { event_name = "protected_token_mismatch", id = result.Id });
                    continue;
                }
                var target = glossary.ApplyLocked(Restore(result.Target, protections), direction);
                cacheWrites[pendingSources[result.Id]] = target;
                if (itemsBySource.TryGetValue(request.Source, out var sourceItems))
                {
                    foreach (var item in sourceItems)
                        byHandle[item.Handle] = result with { Id = item.Handle, Target = target };
                }
                translated++;
            }
        }
        if (cacheWrites.Count > 0) cache.SetMany(cacheWrites);
        WriteLog(log, new { event_name = "translation", immediate, api_unique = translated, unresolved = candidates.Count - byHandle.Count });

        var updated = AutoCadTextRepository.Apply(database, byHandle, log);
        database.SaveAs(outputPath, true, DwgVersion.Current, database.SecurityParameters);
        WriteLog(log, new { event_name = "complete", updated, output = outputPath });
        progress?.Invoke($"完成：回写 {updated} 个对象，输出 {outputPath}");
        return new PipelineSummary(items.Count, candidates.Count, translated + immediate, updated,
            candidates.Count - updated, 0, outputPath, logPath);
    }

    internal static string CacheKey(TranslationDirection direction, string glossaryPath, string source)
    {
        var version = File.Exists(glossaryPath) ? File.GetLastWriteTimeUtc(glossaryPath).Ticks.ToString() : "none";
        return $"{direction}|{version}|{source}";
    }

    private static string Restore(string translated, IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values) translated = translated.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return translated;
    }

    private static void WriteLog(StreamWriter log, object value)
    {
        log.WriteLine(JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, value }));
        log.Flush();
    }
}

