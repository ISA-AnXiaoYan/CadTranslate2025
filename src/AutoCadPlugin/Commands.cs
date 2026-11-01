using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using System.Diagnostics;
using System.Reflection;

namespace CadTranslate2025;

public sealed class Commands
{
    [CommandMethod("CADTRANS", CommandFlags.Session)]
    public void TranslateCurrent()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var editor = doc.Editor;
        if (string.IsNullOrWhiteSpace(doc.Database.Filename))
        {
            editor.WriteMessage("\n请先保存当前DWG，再执行 CADTRANS。\n");
            return;
        }
        var direction = PromptDirection(editor);
        if (direction == null) return;
        var output = MakeOutputPath(doc.Database.Filename, direction.Value);
        RunOne(editor, doc.Database.Filename, output, direction.Value);
    }

    [CommandMethod("CADTRANS_BATCH", CommandFlags.Session)]
    public void TranslateFolder()
    {
        var editor = Application.DocumentManager.MdiActiveDocument.Editor;
        var direction = PromptDirection(editor);
        if (direction == null) return;
        var pathPrompt = new PromptStringOptions("\n输入DWG文件夹路径: ") { AllowSpaces = true };
        var pathResult = editor.GetString(pathPrompt);
        if (pathResult.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pathResult.StringResult)) return;
        var folder = pathResult.StringResult.Trim().Trim('"');
        if (!Directory.Exists(folder))
        {
            editor.WriteMessage($"\n文件夹不存在：{folder}\n");
            return;
        }
        var outputFolder = Path.Combine(folder, $"_CadTranslate_{(direction == TranslationDirection.ChineseToEnglish ? "EN" : "ZH")}");
        Directory.CreateDirectory(outputFolder);
        var files = Directory.EnumerateFiles(folder, "*.dwg", SearchOption.TopDirectoryOnly).ToList();
        editor.WriteMessage($"\n准备处理 {files.Count} 个DWG，输出目录：{outputFolder}\n");
        var success = 0;
        foreach (var source in files)
        {
            var output = Path.Combine(outputFolder, Path.GetFileName(MakeOutputPath(source, direction.Value)));
            try { RunOne(editor, source, output, direction.Value); success++; }
            catch (System.Exception ex) { editor.WriteMessage($"\n失败：{Path.GetFileName(source)} - {ex.Message}\n"); }
        }
        editor.WriteMessage($"\n批处理结束：成功 {success}/{files.Count}。\n");
    }

    [CommandMethod("CADTRANS_TERMS", CommandFlags.Session)]
    public void OpenProjectTerms()
    {
        var path = Paths.GlossaryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.Copy(Paths.TemplateGlossaryPath, path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    [CommandMethod("CADTRANS_STATUS", CommandFlags.Session)]
    public void ShowLargeJobStatus()
    {
        var editor = Application.DocumentManager.MdiActiveDocument.Editor;
        var jobPath = PromptJobPath(editor, "输入大图任务文件路径 (.cadtrans.job.json): ");
        if (jobPath == null) return;
        try
        {
            var job = LargePipeline.ReadJob(jobPath);
            var status = LargePipeline.ReadStatus(jobPath);
            if (status == null)
            {
                editor.WriteMessage("\n未找到任务状态文件。\n");
                return;
            }
            editor.WriteMessage($"\n任务 {job.JobId}: {status.State}，完成 {status.Completed}/{status.Total} 组。\n");
            if (!string.IsNullOrWhiteSpace(status.Message)) editor.WriteMessage($"{status.Message}\n");
            if (!string.IsNullOrWhiteSpace(status.Error)) editor.WriteMessage($"错误：{status.Error}\n");
            editor.WriteMessage($"结果：{job.ResultPath}\n回写命令：CADTRANS_APPLY\n");
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\n读取任务失败：{ex.Message}\n");
        }
    }

    [CommandMethod("CADTRANS_APPLY", CommandFlags.Session)]
    public void ApplyLargeJob()
    {
        var editor = Application.DocumentManager.MdiActiveDocument.Editor;
        var jobPath = PromptJobPath(editor, "输入已完成的大图任务文件路径 (.cadtrans.job.json): ");
        if (jobPath == null) return;
        try
        {
            var summary = LargePipeline.Apply(jobPath);
            editor.WriteMessage($"\n已安全回写 {summary.Updated} 个对象。\n输出：{summary.OutputPath}\n日志：{summary.LogPath}\n");
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\n回写失败：{ex.Message}\n");
        }
    }

    [CommandMethod("CADTRANS_BG_EN2ZH", CommandFlags.Session)]
    public void TranslateBackgroundEnglishToChinese()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var editor = doc.Editor;
        if (string.IsNullOrWhiteSpace(doc.Database.Filename))
        {
            editor.WriteMessage("\n当前图纸没有文件路径，后台任务已停止。\n");
            return;
        }

        try
        {
            var source = FreezeBackgroundInput(doc.Database.Filename);
            var output = MakeBatchOutputPath(doc.Database.Filename, TranslationDirection.EnglishToChinese);
            var summary = TranslationPipeline.Run(source, output,
                TranslationDirection.EnglishToChinese, Paths.GlossaryPath, Paths.CachePath,
                forceLarge: true);
            if (summary.JobPath != null)
            {
                var status = LargePipeline.WaitForCompletion(summary.JobPath);
                editor.WriteMessage($"\n后台任务 {status.State}：{status.Completed}/{status.Total} 组。\n");
                if (status.State is "completed" or "completed_with_warnings")
                {
                    var applied = LargePipeline.Apply(summary.JobPath);
                    editor.WriteMessage($"已回写 {applied.Updated} 个对象：{applied.OutputPath}\n");
                }
                else if (status.State == "failed") editor.WriteMessage($"错误：{status.Error}\n");
            }
            else
            {
                editor.WriteMessage($"\n后台输出：{summary.OutputPath}\n");
            }
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\n后台翻译失败：{ex.Message}\n");
        }
    }

    [CommandMethod("CADTRANS_BG_APPLY_JOB", CommandFlags.Session)]
    public void ApplyBackgroundJobWithoutActiveDrawing()
    {
        var jobPath = Environment.GetEnvironmentVariable("CADTRANS_JOB_PATH");
        if (string.IsNullOrWhiteSpace(jobPath) || !File.Exists(jobPath)) return;
        try
        {
            var summary = LargePipeline.Apply(jobPath);
            Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                $"\n后台回写 {summary.Updated} 个对象：{summary.OutputPath}\n");
        }
        catch (System.Exception ex)
        {
            Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n后台回写失败：{ex.Message}\n");
        }
    }

    private static string FreezeBackgroundInput(string activeSource)
    {
        var freezeRoot = Environment.GetEnvironmentVariable("CADTRANS_FREEZE_ROOT");
        if (string.IsNullOrWhiteSpace(freezeRoot))
            freezeRoot = Path.Combine(Path.GetDirectoryName(activeSource)!, ".cadtrans_input");
        Directory.CreateDirectory(freezeRoot);
        var frozen = Path.Combine(freezeRoot, Path.GetFileName(activeSource));
        if (string.Equals(Path.GetFullPath(frozen), Path.GetFullPath(activeSource), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("后台 frozen input 不能覆盖活动源文件。");

        using var input = new FileStream(activeSource, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(frozen, FileMode.Create, FileAccess.Write, FileShare.Read);
        input.CopyTo(output);
        output.Flush(true);
        return frozen;
    }

    [CommandMethod("CADTRANS_PING", CommandFlags.Session)]
    public void Ping()
    {
        Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nCADTRANS_PING_OK\n");
    }

    private static TranslationDirection? PromptDirection(Editor editor)
    {
        var options = new PromptKeywordOptions("\n翻译方向 [ZH2EN/EN2ZH] <ZH2EN>: ", "ZH2EN EN2ZH")
        { AllowNone = true };
        var result = editor.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel) return null;
        return result.StringResult.Equals("EN2ZH", StringComparison.OrdinalIgnoreCase)
            ? TranslationDirection.EnglishToChinese : TranslationDirection.ChineseToEnglish;
    }

    private static void RunOne(Editor editor, string source, string output, TranslationDirection direction)
    {
        var summary = TranslationPipeline.Run(source, output, direction, Paths.GlossaryPath, Paths.CachePath,
            message => editor.WriteMessage($"\n{Path.GetFileName(source)}: {message}"));
        if (summary.JobPath != null)
        {
            editor.WriteMessage($"\n大图任务已提交，AutoCAD 已返回。\n任务：{summary.JobPath}\n");
            editor.WriteMessage("翻译完成后执行 CADTRANS_STATUS 查看状态，再执行 CADTRANS_APPLY 回写。\n");
        }
        else
        {
            editor.WriteMessage($"\n已输出：{summary.OutputPath}\n日志：{summary.LogPath}\n");
        }
    }

    private static string? PromptJobPath(Editor editor, string prompt)
    {
        var options = new PromptStringOptions("\n" + prompt) { AllowSpaces = true };
        var result = editor.GetString(options);
        if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult)) return null;
        var path = result.StringResult.Trim().Trim('"');
        if (!File.Exists(path))
        {
            editor.WriteMessage($"\n任务文件不存在：{path}\n");
            return null;
        }
        return path;
    }

    private static string MakeOutputPath(string source, TranslationDirection direction)
    {
        var suffix = direction == TranslationDirection.ChineseToEnglish ? "_EN" : "_ZH";
        var directory = Path.GetDirectoryName(source)!;
        var stem = Path.GetFileNameWithoutExtension(source);
        var candidate = Path.Combine(directory, stem + suffix + ".dwg");
        var index = 2;
        while (File.Exists(candidate)) candidate = Path.Combine(directory, $"{stem}{suffix}_{index++}.dwg");
        return candidate;
    }

    private static string MakeBatchOutputPath(string source, TranslationDirection direction)
    {
        var root = Environment.GetEnvironmentVariable("CADTRANS_OUTPUT_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return MakeOutputPath(source, direction);
        Directory.CreateDirectory(root);
        var suffix = direction == TranslationDirection.ChineseToEnglish ? "_EN" : "_ZH";
        return Path.Combine(root, Path.GetFileNameWithoutExtension(source) + suffix + ".dwg");
    }
}

internal static class Paths
{
    private static readonly string AssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    private static readonly string BundleRoot = Directory.GetParent(AssemblyDir)?.Parent?.FullName ?? AssemblyDir;
    private static readonly string ConfigDir = new[]
    {
        Path.Combine(AssemblyDir, "Config"),
        Path.Combine(BundleRoot, "Contents", "Config"),
        Path.Combine(BundleRoot, "Config")
    }.FirstOrDefault(Directory.Exists) ?? Path.Combine(BundleRoot, "Contents", "Config");
    public static string GlossaryPath => Path.Combine(ConfigDir, "project_terms.csv");
    public static string TemplateGlossaryPath => Path.Combine(ConfigDir, "project_terms.template.csv");
    public static string CachePath => Environment.GetEnvironmentVariable("CADTRANS_CACHE_PATH")
        ?? Path.Combine(ConfigDir, "translation_memory.json");
}

