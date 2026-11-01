using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CadTranslate2025;

public static class AutoCadTextRepository
{
    public static List<CadTextItem> Scan(Database database)
    {
        var items = new List<CadTextItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var tr = database.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId btrId in blockTable)
        {
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            if (btr.IsFromExternalReference || btr.IsDependent) continue;
            var location = btr.IsLayout ? $"Layout:{btr.LayoutId}" : $"Block:{btr.Name}";
            foreach (ObjectId entityId in btr)
            {
                if (!entityId.IsValid || !seen.Add(entityId.Handle.ToString())) continue;
                var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                if (entity != null && TryRead(entity, out var kind, out var source))
                    items.Add(Make(entityId, kind, source, location, entity.Layer, btr.Name));

                if (entity is BlockReference br && br.AttributeCollection.Count > 0)
                {
                    foreach (ObjectId attrId in br.AttributeCollection)
                    {
                        if (!attrId.IsValid || !seen.Add(attrId.Handle.ToString())) continue;
                        if (tr.GetObject(attrId, OpenMode.ForRead) is AttributeReference attr && !string.IsNullOrWhiteSpace(attr.TextString))
                            items.Add(Make(attrId, CadTextKind.AttributeReference, attr.TextString, $"{location}/Insert:{br.Handle}", attr.Layer, btr.Name, attr.Tag));
                    }
                }
            }
        }
        tr.Commit();
        return items;
    }

    public static int Apply(Database database, IReadOnlyDictionary<string, TranslationResult> translations, TextWriter log)
    {
        var updated = 0;
        using var tr = database.TransactionManager.StartTransaction();
        var unlockedLayers = UnlockLockedLayers(database, tr);
        try
        {
            foreach (var pair in translations)
            {
                var result = pair.Value;
                if (!TryFindByHandle(database, tr, result.Id, out var objectId))
                {
                    log.WriteLine($"MISSING|{result.Id}|{result.Source}");
                    continue;
                }
                Entity? entity;
                try
                {
                    entity = tr.GetObject(objectId, OpenMode.ForWrite) as Entity;
                }
                catch (Exception ex)
                {
                    log.WriteLine($"ERROR|{result.Id}|{ex.GetType().Name}|{ex.Message}");
                    continue;
                }
                if (entity == null || !TryRead(entity, out _, out var actual) || !TextUtil.Equivalent(actual, result.Source))
                {
                    log.WriteLine($"SOURCE_MISMATCH|{result.Id}|{result.Source}");
                    continue;
                }
                try
                {
                    Write(entity, result.Target);
                    updated++;
                    log.WriteLine($"UPDATED|{result.Id}");
                }
                catch (Exception ex)
                {
                    log.WriteLine($"ERROR|{result.Id}|{ex.GetType().Name}|{ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var layer in unlockedLayers)
            {
                try { layer.IsLocked = true; } catch { /* preserve best-effort lock state */ }
            }
        }
        tr.Commit();
        return updated;
    }

    private static List<LayerTableRecord> UnlockLockedLayers(Database database, Transaction tr)
    {
        var unlocked = new List<LayerTableRecord>();
        try
        {
            var layers = (LayerTable)tr.GetObject(database.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId layerId in layers)
            {
                try
                {
                    var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                    if (!layer.IsLocked) continue;
                    layer.UpgradeOpen();
                    layer.IsLocked = false;
                    unlocked.Add(layer);
                }
                catch { /* dependent/XREF layers may be non-writable */ }
            }
        }
        catch { /* a database without a readable layer table remains fail-closed */ }
        return unlocked;
    }

    private static bool TryFindByHandle(Database database, Transaction tr, string handle, out ObjectId objectId)
    {
        objectId = ObjectId.Null;
        if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, null, out var value)) return false;
        try
        {
            objectId = database.GetObjectId(false, new Handle(value), 0);
            return objectId.IsValid;
        }
        catch { return false; }
    }

    private static CadTextItem Make(ObjectId id, CadTextKind kind, string source, string location, string layer, string block, string? tag = null)
        => new(id, id.Handle.ToString(), kind, source, location, layer, block, tag);

    private static bool TryRead(Entity entity, out CadTextKind kind, out string source)
    {
        switch (entity)
        {
            case DBText text when !string.IsNullOrWhiteSpace(text.TextString):
                kind = entity is AttributeDefinition ? CadTextKind.AttributeDefinition : CadTextKind.DbText;
                source = text.TextString;
                return true;
            case MText mtext when !string.IsNullOrWhiteSpace(mtext.Contents):
                kind = CadTextKind.MText;
                source = mtext.Contents;
                return true;
            case MLeader leader:
                kind = CadTextKind.MLeader;
                return TryReadMLeader(leader, out source);
            case AttributeReference attr when !string.IsNullOrWhiteSpace(attr.TextString):
                kind = CadTextKind.AttributeReference;
                source = attr.TextString;
                return true;
            default:
                kind = default;
                source = "";
                return false;
        }
    }

    private static void Write(Entity entity, string target)
    {
        switch (entity)
        {
            case DBText text:
                text.TextString = target.Replace("\n", " ");
                break;
            case MText mtext:
                mtext.Contents = target.Replace("\n", "\\P");
                break;
            case MLeader leader:
                try
                {
                    var leaderText = leader.MText;
                    if (leaderText == null) throw new InvalidOperationException("MLeader has no MText content");
                    leaderText.Contents = target.Replace("\n", "\\P");
                    leader.MText = leaderText;
                    break;
                }
                catch (System.Exception ex)
                {
                    throw new NotSupportedException("MLeader content is not writable as MText", ex);
                }
            default:
                throw new NotSupportedException(entity.GetType().Name);
        }
    }

    private static bool TryReadMLeader(MLeader leader, out string source)
    {
        try
        {
            var mtext = leader.MText;
            source = mtext?.Contents ?? "";
            return !string.IsNullOrWhiteSpace(source);
        }
        catch (System.Exception)
        {
            source = "";
            return false;
        }
    }
}

public static class TextUtil
{
    private static readonly Regex MTextFormatting = new(@"\\(?:[HhWwCcFfTtQqAa])[^;{}]*;|[{}]", RegexOptions.Compiled);
    private static readonly Regex MTextStacked = new(@"\\S([^;]+);", RegexOptions.Compiled);

    public static string ToPlainText(string value)
    {
        var text = value.Replace("\\P", "\n", StringComparison.OrdinalIgnoreCase);
        text = MTextStacked.Replace(text, "$1");
        text = MTextFormatting.Replace(text, "");
        return text.Replace("\\~", " ").Trim();
    }

    public static string Normalize(string value) => Regex.Replace(ToPlainText(value), @"\s+", " ").Trim();
    public static bool Equivalent(string left, string right) => Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);
    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

