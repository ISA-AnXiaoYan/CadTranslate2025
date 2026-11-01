namespace CadTranslate2025;

public enum TranslationDirection
{
    ChineseToEnglish,
    EnglishToChinese
}

public enum CadTextKind
{
    DbText,
    MText,
    MLeader,
    AttributeReference,
    AttributeDefinition
}

public sealed record CadTextItem(
    Autodesk.AutoCAD.DatabaseServices.ObjectId ObjectId,
    string Handle,
    CadTextKind Kind,
    string Source,
    string Location,
    string Layer,
    string? BlockName = null,
    string? AttributeTag = null);

public sealed record TranslationRequest(
    string Id,
    string Source,
    string ProtectedSource,
    TranslationDirection Direction,
    IReadOnlyList<GlossaryTerm> RelevantTerms);

public sealed record TranslationResult(
    string Id,
    string Source,
    string Target,
    string? Confidence,
    string? Warning,
    bool FromGlossary,
    bool FromCache);

public sealed record GlossaryTerm(
    string Zh,
    string En,
    string[] Aliases,
    string MatchMode,
    bool Locked,
    string Project,
    string Notes);

public sealed record PipelineSummary(
    int Scanned,
    int Candidates,
    int Translated,
    int Updated,
    int Skipped,
    int Errors,
    string OutputPath,
    string LogPath,
    string? JobPath = null);

public sealed record LargeTranslationGroup(
    string Id,
    string Source,
    string ProtectedSource,
    TranslationDirection Direction,
    List<GlossaryTerm> RelevantTerms,
    List<string> Handles,
    string? InitialTarget = null,
    bool InitialFromCache = false);

public sealed record LargeTranslationJob(
    string JobId,
    string SourcePath,
    string OutputPath,
    string LogPath,
    string ResultPath,
    string StatusPath,
    string SourceSha256,
    string GlossaryPath,
    string CachePath,
    TranslationDirection Direction,
    int Scanned,
    int Candidates,
    List<LargeTranslationGroup> Groups);

public sealed record LargeTranslationOutput(
    string JobId,
    Dictionary<string, TranslationResult> Translations);

public sealed record LargeJobStatus(
    string JobId,
    string State,
    int Completed,
    int Total,
    string? Message,
    string? Error,
    DateTimeOffset UpdatedAt);

