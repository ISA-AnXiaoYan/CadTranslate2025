using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

public class PreviewApply
{
    static readonly JsonSerializerOptions Opt=new(){WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    static string Hash(string path){using var s=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(s));}
    static CellContent TableContent(Table table, Entry item)
    {
        var cell=table.Cells[item.Row ?? throw new ArgumentException("Missing row"),item.Column ?? throw new ArgumentException("Missing column")];
        if(cell.Contents.Count!=1)throw new InvalidOperationException("Expected a single cell content");
        var content=cell.Contents[0];
        if(content.HasFormula || !content.FieldId.IsNull || !content.BlockTableRecordId.IsNull)
            throw new InvalidOperationException("Protected cell content");
        return content;
    }
    static string Read(Entity e, Entry item)
    {
        if(e is MLeader leader){using var text=leader.MText;return text.Contents;}
        return e switch{DBText d=>d.TextString,MText m=>m.Contents,Table t=>TableContent(t,item).TextString,_=>throw new InvalidOperationException("Unsupported type")};
    }
    static ObjectId Style(Entity e, Entry item)
    {
        if(e is MLeader leader){using var text=leader.MText;return text.TextStyleId;}
        return e switch{DBText d=>d.TextStyleId,MText m=>m.TextStyleId,Table t=>TableContent(t,item).TextStyleId,_=>ObjectId.Null};
    }
    static Dictionary<string,bool> Locks(Database db,Transaction tr)
    {var result=new Dictionary<string,bool>();foreach(ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead)){var l=(LayerTableRecord)tr.GetObject(id,OpenMode.ForRead);result[l.Name]=l.IsLocked;}return result;}
    [CommandMethod("CADTRANS_HEADLESS_APPLY",CommandFlags.Session)]
    public void Run()
    {
        string root=Environment.GetEnvironmentVariable("CADTRANS_WORK_ROOT")!;
        var planPath=Path.Combine(root,"preview_plan.json");
        var planHash=Hash(planPath);
        var plans=JsonSerializer.Deserialize<List<Plan>>(File.ReadAllText(planPath))!;
        var reports=new List<object>();
        foreach(var plan in plans)
        {
            var expected=new Dictionary<Entry,string>();var errors=new List<string>();
            var input=Path.Combine(root,"preview_inputs",plan.File);
            var output=Path.Combine(root,"preview_outputs",Path.GetDirectoryName(plan.File)!,Path.GetFileNameWithoutExtension(plan.File)+"_ZH.dwg");
            var before=Hash(input);Dictionary<string,bool> locks;
            try
            {
                using(var db=new Database(false,true))
                {
                    db.ReadDwgFile(input,FileOpenMode.OpenForReadAndAllShare,false,null);
                    using(var tr=db.TransactionManager.StartTransaction())
                    {
                        locks=Locks(db,tr);
                        ObjectId styleId=YaHeiStyle.Ensure(db,tr);
                        foreach(var item in plan.Entries.Where(x=>x.Target!=x.Source))
                        {
                            var id=db.GetObjectId(false,new Handle(Convert.ToInt64(item.Handle,16)),0);
                            var entity=(Entity)tr.GetObject(id,OpenMode.ForRead);
                            if(Read(entity,item)!=item.Source)throw new System.Exception("Source mismatch "+item.Handle);
                            var layer=(LayerTableRecord)tr.GetObject(entity.LayerId,OpenMode.ForRead);var wasLocked=layer.IsLocked;
                            if(wasLocked)
                            {
                                if(!layer.IsWriteEnabled)layer.UpgradeOpen();
                                layer.IsLocked=false;
                            }
                            if(!entity.IsWriteEnabled)entity.UpgradeOpen();
                            string target=item.Target;
                            switch(entity)
                            {
                                case DBText t:t.TextString=target;t.TextStyleId=styleId;break;
                                case MText t:
                                    target=YaHeiStyle.Contents(target);t.Contents=target;t.TextStyleId=styleId;break;
                                case MLeader l:
                                    using(var t=l.MText){t.Contents=YaHeiStyle.Contents(target);t.TextStyleId=styleId;l.MText=t;target=t.Contents;}break;
                                case Table table:
                                    var content=TableContent(table,item);
                                    content.TextString=YaHeiStyle.Contents(target);
                                    content.TextStyleId=styleId;
                                    break;
                                default:throw new System.Exception("Unhandled text "+entity.GetType().Name);
                            }
                            expected[item]=Read(entity,item);
                            if(wasLocked)layer.IsLocked=true;
                        }
                        var restored=Locks(db,tr);
                        if(locks.Count!=restored.Count||locks.Any(p=>!restored.TryGetValue(p.Key,out var value)||value!=p.Value))
                            throw new InvalidOperationException("Layer lock restore failed");
                        tr.Commit();
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    if(File.Exists(output))throw new System.Exception("Output already exists");
                    db.SaveAs(output,true,DwgVersion.Current,db.SecurityParameters);
                }
                using(var verify=new Database(false,true))
                {
                    verify.ReadDwgFile(output,FileOpenMode.OpenForReadAndAllShare,false,null);
                    using var tx=verify.TransactionManager.StartTransaction();
                    var styleTable=(TextStyleTable)tx.GetObject(verify.TextStyleTableId,OpenMode.ForRead);
                    var styleId=styleTable[YaHeiStyle.Name];
                    var style=(TextStyleTableRecord)tx.GetObject(styleId,OpenMode.ForRead);
                    if(style.Font.TypeFace is not ("Microsoft YaHei" or "微软雅黑") || style.Font.CharacterSet!=134 || style.IsShapeFile)
                        errors.Add("Chinese font descriptor mismatch");
                    foreach(var p in expected)
                    {
                        var id=verify.GetObjectId(false,new Handle(Convert.ToInt64(p.Key.Handle,16)),0);
                        var entity=(Entity)tx.GetObject(id,OpenMode.ForRead);
                        if(Read(entity,p.Key)!=p.Value)errors.Add("Readback mismatch "+p.Key.Handle);
                        if(Style(entity,p.Key)!=styleId)errors.Add("Style mismatch "+p.Key.Handle);
                    }
                    var outLocks=Locks(verify,tx);if(locks.Count!=outLocks.Count||locks.Any(p=>outLocks[p.Key]!=p.Value))errors.Add("Layer locks changed");
                }
                if(Hash(input)!=before)errors.Add("Input hash changed");
                var savedText=expected.Select(x=>new{x.Key.Handle,x.Key.Row,x.Key.Column,Raw=x.Value});
                reports.Add(new{File=plan.File,Output=output,Changed=expected.Count,ReadbackChecked=expected.Count,SourceSha256=before,OutputSha256=Hash(output),PlanSha256=planHash,SavedText=savedText,Errors=errors,LayerLocksPreserved=!errors.Contains("Layer locks changed")});
            }
            catch(System.Exception ex){reports.Add(new{File=plan.File,Error=ex.ToString()});}
        }
        File.WriteAllText(Path.Combine(root,"preview_apply_report.json"),JsonSerializer.Serialize(reports,Opt));
        Environment.Exit(0);
    }
    record Plan(string File,List<Entry> Entries);
    record Entry(string Handle,string Kind,string Source,string Target,int? Row=null,int? Column=null);
}
