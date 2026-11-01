using Autodesk.AutoCAD.DatabaseServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CadTranslate2025;

/// <summary>
/// 大图（候选文字超过阈值）的后台翻译与分阶段回写支持。
/// 后台阶段只处理普通 .NET 数据和文件，不接触 AutoCAD API。
/// </summary>
public static class LargePipeline
{
    public const int LargeThreshold = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private static readonly ConcurrentDictionary<string, Task> Running = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, object> CacheLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string JobPath(string outputPath) => Path.ChangeExtension(outputPath, ".cadtrans.job.json");
    public static string ResultPath(string outputPath) => Path.ChangeExtension(outputPath, ".cadtrans.result.json");
    public static string StatusPath(string outputPath) => Path.ChangeExtension(outputPath, ".cadtrans.status.json");
    public static string LogPath(string outputPath) => Path.ChangeExtension(outputPath, ".translation.log.jsonl");

    public static PipelineSummary Start(
        string sourcePath,
        string outputPath,
        TranslationDirection direction,
        string glossaryPath,
        string cachePath,
        int scanned,
        IReadOnlyList<CadTextItem> candidates,
        Action<string>? progress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var jobPath = JobPath(outputPath);
        var sourceSha256 = Sha256File(sourcePath);
        var job = TryRead<LargeTranslationJob>(jobPath);
        var isUsable = job != null &&
            string.Equals(job.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(job.SourcePath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(job.OutputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase);

        if (!isUsable)
        {
            var glossary = Glossary.Load(glossaryPath);
            var cache = TranslationMemory.Load(cachePath);
            job = BuildJob(sourcePath, outputPath, direction, glossaryPath, cachePath,
                scanned, candidates, sourceSha256, glossary, cache);
            WriteJsonAtomic(jobPath, job);
            WriteStatus(job, "created", InitialCompleted(job), job.Groups.Count, "任务已创建，等待后台翻译", null);
            AppendLog(job.LogPath, new
            {
                event_name = "scan",
                source = sourcePath,
                scanned,
                candidates = candidates.Count,
                unique_groups = job.Groups.Count,
                direction = direction.ToString(),
                mode = "large_async"
            });
        }

        var activeJob = job ?? throw new InvalidOperationException("大图任务创建失败。");

        var status = TryRead<LargeJobStatus>(activeJob.StatusPath);
        if (status?.State is not ("completed" or "applied"))
            Launch(jobPath, activeJob.JobId);

        progress?.Invoke($"大图模式：{candidates.Count} 个候选，去重为 {activeJob.Groups.Count} 组；后台任务已提交");
        return new PipelineSummary(scanned, candidates.Count, InitialCompleted(activeJob), 0,
            candidates.Count - InitialCompleted(activeJob), 0, outputPath, activeJob.LogPath, jobPath);
    }

    public static LargeTranslationJob ReadJob(string jobPath)
        => TryRead<LargeTranslationJob>(jobPath) ?? throw new InvalidOperationException($"任务文件无法读取：{jobPath}");

    public static LargeJobStatus? ReadStatus(string jobPath)
    {
        var job = ReadJob(jobPath);
        return TryRead<LargeJobStatus>(job.StatusPath);
    }

    public static LargeJobStatus WaitForCompletion(string jobPath, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromHours(12));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = ReadStatus(jobPath);
            if (status?.State is "completed" or "completed_with_warnings" or "failed" or "applied")
                return status;
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
        throw new TimeoutException($"后台任务等待超时：{jobPath}");
    }

    public static PipelineSummary Apply(string jobPath)
    {
        var job = ReadJob(jobPath);
        var status = TryRead<LargeJobStatus>(job.StatusPath);
        if (status == null)
            throw new InvalidOperationException("任务状态文件不存在，不能安全回写。");
        if (status.State == "applied")
            return new PipelineSummary(job.Scanned, job.Candidates, status.Completed, 0, job.Candidates, 0,
                job.OutputPath, job.LogPath, jobPath);
        if (status.State is not ("completed" or "completed_with_warnings"))
            throw new InvalidOperationException($"任务尚未完成：{status.State}。请先用 CADTRANS_STATUS 查看状态。");

        var currentSha256 = Sha256File(job.SourcePath);
        if (!string.Equals(currentSha256, job.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("源DWG在翻译后发生变化，已阻止回写；请重新扫描生成任务。");

        var output = TryRead<LargeTranslationOutput>(job.ResultPath);
        if (output == null)
            throw new InvalidOperationException("翻译结果文件不存在，不能安全回写。");

        var byHandle = new Dictionary<string, TranslationResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in job.Groups)
        {
            if (!output.Translations.TryGetValue(group.Id, out var result)) continue;
            foreach (var handle in group.Handles)
                byHandle[handle] = result with { Id = handle, Source = group.Source };
        }

        using var database = new Database(false, true);
        database.ReadDwgFile(job.SourcePath, FileOpenMode.OpenForReadAndAllShare, false, null);
        using var log = new StreamWriter(job.LogPath, true, Encoding.UTF8);
        var updated = 0;
        var stage = 0;
        foreach (var chunk in byHandle.Chunk(1000))
        {
            var stageMap = chunk.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            updated += AutoCadTextRepository.Apply(database, stageMap, log);
            stage++;
            WriteLog(log, new { event_name = "apply_stage", stage, stage_items = stageMap.Count, updated });
        }
        database.SaveAs(job.OutputPath, true, DwgVersion.Current, database.SecurityParameters);
        WriteLog(log, new { event_name = "complete", updated, output = job.OutputPath, mode = "large_apply" });
        WriteStatus(job, "applied", output.Translations.Count, job.Groups.Count,
            status.State == "completed_with_warnings" ? "已回写已完成结果，仍有未解析组" : "已完成回写", null);
        return new PipelineSummary(job.Scanned, job.Candidates, output.Translations.Count, updated,
            job.Candidates - updated, 0, job.OutputPath, job.LogPath, jobPath);
    }

    private static LargeTranslationJob BuildJob(
        string sourcePath,
        string outputPath,
        TranslationDirection direction,
        string glossaryPath,
        string cachePath,
        int scanned,
        IReadOnlyList<CadTextItem> candidates,
        string sourceSha256,
        Glossary glossary,
        TranslationMemory cache)
    {
        var builders = new Dictionary<string, GroupBuilder>(StringComparer.Ordinal);
        foreach (var item in candidates)
        {
            var source = TextUtil.Normalize(item.Source);
            var id = TextUtil.Sha256(direction + "|" + source)[..16];
            if (!builders.TryGetValue(id, out var group))
            {
                var cacheKey = TranslationPipeline.CacheKey(direction, glossaryPath, source);
                string? initialTarget = null;
                var fromCache = false;
                if (glossary.TryExact(source, direction, out var exact))
                    initialTarget = exact;
                else if (cache.TryGet(cacheKey, out var cached))
                {
                    initialTarget = glossary.ApplyLocked(cached, direction);
                    fromCache = true;
                }

                group = new GroupBuilder
                {
                    Id = id,
                    Source = source,
                    ProtectedSource = TextProtection.Protect(source, out _),
                    Direction = direction,
                    RelevantTerms = glossary.Relevant(source).ToList(),
                    InitialTarget = initialTarget,
                    InitialFromCache = fromCache
                };
                builders.Add(id, group);
            }
            group.Handles.Add(item.Handle);
        }

        var groups = builders.Values.Select(x => new LargeTranslationGroup(
            x.Id, x.Source, x.ProtectedSource, x.Direction, x.RelevantTerms, x.Handles,
            x.InitialTarget, x.InitialFromCache)).ToList();
        var jobId = TextUtil.Sha256(sourcePath + "|" + outputPath + "|" + sourceSha256 + "|" + direction)[..24];
        return new LargeTranslationJob(jobId, sourcePath, outputPath, LogPath(outputPath),
            ResultPath(outputPath), StatusPath(outputPath), sourceSha256, glossaryPath, cachePath,
            direction, scanned, candidates.Count, groups);
    }

    private static void Launch(string jobPath, string jobId)
    {
        if (!Running.TryAdd(jobId, Task.CompletedTask)) return;
        var task = Task.Run(() => ProcessAsync(jobPath, jobId));
        Running[jobId] = task;
    }

    private static async Task ProcessAsync(string jobPath, string jobId)
    {
        LargeTranslationJob? job = null;
        try
        {
            job = ReadJob(jobPath);
            var glossary = Glossary.Load(job.GlossaryPath);
            var cache = TranslationMemory.Load(job.CachePath);
            var output = TryRead<LargeTranslationOutput>(job.ResultPath)
                ?? new LargeTranslationOutput(job.JobId, new Dictionary<string, TranslationResult>(StringComparer.Ordinal));
            if (output.Translations == null)
                output = new LargeTranslationOutput(job.JobId, new Dictionary<string, TranslationResult>(StringComparer.Ordinal));

            foreach (var group in job.Groups.Where(x => x.InitialTarget != null))
            {
                if (!output.Translations.ContainsKey(group.Id))
                    output.Translations[group.Id] = new TranslationResult(group.Id, group.Source, group.InitialTarget!,
                        group.InitialFromCache ? "cache" : "glossary", null, !group.InitialFromCache, group.InitialFromCache);
            }
            WriteResult(job, output);
            WriteStatus(job, "running", output.Translations.Count, job.Groups.Count, "后台翻译中", null);
            AppendLog(job.LogPath, new { event_name = "async_started", completed = output.Translations.Count, total = job.Groups.Count });

            var pending = job.Groups.Where(x => !output.Translations.ContainsKey(x.Id)).ToList();
            var warningIds = new HashSet<string>(StringComparer.Ordinal);
            if (pending.Count > 0)
            {
                using var client = new DeepSeekClient();
                var groupsById = pending.ToDictionary(x => x.Id, StringComparer.Ordinal);
                var sync = new object();
                using var limiter = new SemaphoreSlim(MaxApiConcurrency(), MaxApiConcurrency());
                var completedBatches = 0;
                var tasks = pending.Chunk(80).Select(async batch =>
                {
                    await limiter.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var requests = batch.Select(x => new TranslationRequest(x.Id, x.Source, x.ProtectedSource,
                            x.Direction, x.RelevantTerms)).ToArray();
                        var responses = await client.TranslateAsync(requests).ConfigureAwait(false);
                        lock (sync)
                        {
                            var cacheWrites = new Dictionary<string, string>(StringComparer.Ordinal);
                            var responseIds = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var response in responses)
                            {
                                if (!responseIds.Add(response.Id)) continue;
                                if (!groupsById.TryGetValue(response.Id, out var group)) continue;
                                if (!TextProtection.TryRestore(response.Target, group.Source, out var restored))
                                {
                                    warningIds.Add(group.Id);
                                    AppendLog(job.LogPath, new { event_name = "protected_token_mismatch", id = group.Id });
                                    continue;
                                }
                                var target = glossary.ApplyLocked(restored, group.Direction);
                                warningIds.Remove(group.Id);
                                output.Translations[group.Id] = response with { Source = group.Source, Target = target };
                                cacheWrites[TranslationPipeline.CacheKey(group.Direction, job.GlossaryPath, group.Source)] = target;
                            }
                            if (cacheWrites.Count > 0)
                            {
                                var cacheLock = CacheLocks.GetOrAdd(job.CachePath, _ => new object());
                                lock (cacheLock) cache.SetMany(cacheWrites);
                            }
                            var batchNumber = ++completedBatches;
                            WriteResult(job, output);
                            WriteStatus(job, "running", output.Translations.Count, job.Groups.Count,
                                $"已完成 {output.Translations.Count}/{job.Groups.Count} 组", null);
                            AppendLog(job.LogPath, new { event_name = "batch", batch = batchNumber,
                                completed = output.Translations.Count, total = job.Groups.Count });
                        }
                    }
                    finally
                    {
                        limiter.Release();
                    }
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);

                var retryPending = pending.Where(x => !output.Translations.ContainsKey(x.Id)).ToList();
                foreach (var batch in retryPending.Chunk(10))
                {
                    var requests = batch.Select(x => new TranslationRequest(x.Id, x.Source, x.ProtectedSource,
                        x.Direction, x.RelevantTerms)).ToArray();
                    var responses = await client.TranslateAsync(requests).ConfigureAwait(false);
                    lock (sync)
                    {
                        var cacheWrites = new Dictionary<string, string>(StringComparer.Ordinal);
                        var responseIds = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var response in responses)
                        {
                            if (!responseIds.Add(response.Id)) continue;
                            if (!groupsById.TryGetValue(response.Id, out var group)) continue;
                            if (!TextProtection.TryRestore(response.Target, group.Source, out var restored))
                            {
                                warningIds.Add(group.Id);
                                AppendLog(job.LogPath, new { event_name = "protected_token_mismatch_retry", id = group.Id });
                                continue;
                            }
                            var target = glossary.ApplyLocked(restored, group.Direction);
                            warningIds.Remove(group.Id);
                            output.Translations[group.Id] = response with { Source = group.Source, Target = target };
                            cacheWrites[TranslationPipeline.CacheKey(group.Direction, job.GlossaryPath, group.Source)] = target;
                        }
                        if (cacheWrites.Count > 0)
                        {
                            var cacheLock = CacheLocks.GetOrAdd(job.CachePath, _ => new object());
                            lock (cacheLock) cache.SetMany(cacheWrites);
                        }
                        var batchNumber = ++completedBatches;
                        WriteResult(job, output);
                        WriteStatus(job, "running", output.Translations.Count, job.Groups.Count,
                            $"已完成 {output.Translations.Count}/{job.Groups.Count} 组", null);
                        AppendLog(job.LogPath, new { event_name = "retry_batch", batch = batchNumber,
                            completed = output.Translations.Count, total = job.Groups.Count });
                    }
                }
            }

            var unresolved = job.Groups.Count - output.Translations.Count;
            var warnings = warningIds.Count;
            WriteResult(job, output);
            WriteStatus(job, unresolved == 0 && warnings == 0 ? "completed" : "completed_with_warnings",
                output.Translations.Count, job.Groups.Count,
                unresolved == 0 ? "翻译完成，可执行 CADTRANS_APPLY" : $"仍有 {unresolved} 组未完成，可执行 CADTRANS_APPLY 回写已完成组", null);
            AppendLog(job.LogPath, new { event_name = "translation", completed = output.Translations.Count, total = job.Groups.Count, unresolved, warnings });
        }
        catch (Exception ex)
        {
            if (job != null)
            {
                WriteStatus(job, "failed", TryRead<LargeTranslationOutput>(job.ResultPath)?.Translations.Count ?? 0,
                    job.Groups.Count, "后台任务失败，可重新执行 CADTRANS 续跑", ex.Message);
                AppendLog(job.LogPath, new { event_name = "error", error = ex.Message, type = ex.GetType().Name });
            }
        }
        finally
        {
            Running.TryRemove(jobId, out _);
        }
    }

    private static int InitialCompleted(LargeTranslationJob job) => job.Groups.Count(x => x.InitialTarget != null);

    private static int MaxApiConcurrency()
    {
        var raw = Environment.GetEnvironmentVariable("CADTRANS_MAX_API_CONCURRENCY");
        return int.TryParse(raw, out var value) ? Math.Clamp(value, 1, 2500) : 1;
    }

    private static void WriteResult(LargeTranslationJob job, LargeTranslationOutput output)
        => WriteJsonAtomic(job.ResultPath, output);

    private static void WriteStatus(LargeTranslationJob job, string state, int completed, int total, string message, string? error)
        => WriteJsonAtomic(job.StatusPath, new LargeJobStatus(job.JobId, state, completed, total, message, error, DateTimeOffset.UtcNow));

    private static T? TryRead<T>(string path) where T : class
    {
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions); }
        catch { return default; }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8);
        File.Move(temp, path, true);
    }

    private static void AppendLog(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var log = new StreamWriter(path, true, Encoding.UTF8);
        WriteLog(log, value);
    }

    private static void WriteLog(StreamWriter log, object value)
    {
        log.WriteLine(JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, value }, JsonOptions));
        log.Flush();
    }

    private static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class GroupBuilder
    {
        public required string Id { get; init; }
        public required string Source { get; init; }
        public required string ProtectedSource { get; init; }
        public required TranslationDirection Direction { get; init; }
        public required List<GlossaryTerm> RelevantTerms { get; init; }
        public string? InitialTarget { get; init; }
        public bool InitialFromCache { get; init; }
        public List<string> Handles { get; } = [];
    }
}

