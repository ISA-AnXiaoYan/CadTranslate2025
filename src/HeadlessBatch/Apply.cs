using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

public class PreviewApply
{
    static readonly JsonSerializerOptions Opt=new(){WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    static string Hash(string path){using var s=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(s));}
    static string Read(Entity e)=>e switch{DBText d=>d.TextString,MText m=>m.Contents,MLeader l=>l.MText.Contents,_=>throw new System.Exception("Unsupported type")};
    static Dictionary<string,bool> Locks(Database db,Transaction tr)
    {var result=new Dictionary<string,bool>();foreach(ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead)){var l=(LayerTableRecord)tr.GetObject(id,OpenMode.ForRead);result[l.Name]=l.IsLocked;}return result;}
    [CommandMethod("CADTRANS_HEADLESS_APPLY",CommandFlags.Session)]
    public void Run()
    {
        string root=Environment.GetEnvironmentVariable("CADTRANS_WORK_ROOT")!;
        var plans=JsonSerializer.Deserialize<List<Plan>>(File.ReadAllText(Path.Combine(root,"preview_plan.json")))!;
        var reports=new List<object>();
        foreach(var plan in plans)
        {
            var expected=new Dictionary<string,string>();var errors=new List<string>();
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
                            if(Read(entity)!=item.Source)throw new System.Exception("Source mismatch "+item.Handle);
                            var layer=(LayerTableRecord)tr.GetObject(entity.LayerId,OpenMode.ForRead);var wasLocked=layer.IsLocked;
                            if(wasLocked){layer.UpgradeOpen();layer.IsLocked=false;}
                            entity.UpgradeOpen();string target=item.Target;
                            switch(entity)
                            {
                                case DBText t:t.TextString=target;t.TextStyleId=styleId;break;
                                case MText t:
                                    target=YaHeiStyle.Contents(target);t.Contents=target;t.TextStyleId=styleId;break;
                                case MLeader l:
                                    using(var t=l.MText){t.Contents=YaHeiStyle.Contents(target);t.TextStyleId=styleId;l.MText=t;target=t.Contents;}break;
                                default:throw new System.Exception("Unhandled text "+entity.GetType().Name);
                            }
                            expected[item.Handle]=Read(entity);
                            if(wasLocked)layer.IsLocked=true;
                        }
                        if(locks.Any(p=>Locks(db,tr)[p.Key]!=p.Value))throw new System.Exception("Layer lock restore failed");
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
                    foreach(var p in expected){var id=verify.GetObjectId(false,new Handle(Convert.ToInt64(p.Key,16)),0);if(Read((Entity)tx.GetObject(id,OpenMode.ForRead))!=p.Value)errors.Add("Readback mismatch "+p.Key);}
                    var outLocks=Locks(verify,tx);if(locks.Count!=outLocks.Count||locks.Any(p=>outLocks[p.Key]!=p.Value))errors.Add("Layer locks changed");
                }
                if(Hash(input)!=before)errors.Add("Input hash changed");
                reports.Add(new{File=plan.File,Output=output,Changed=expected.Count,ReadbackChecked=expected.Count,SourceSha256=before,OutputSha256=Hash(output),Errors=errors,LayerLocksPreserved=!errors.Contains("Layer locks changed")});
            }
            catch(System.Exception ex){reports.Add(new{File=plan.File,Error=ex.ToString()});}
        }
        File.WriteAllText(Path.Combine(root,"preview_apply_report.json"),JsonSerializer.Serialize(reports,Opt));
        Environment.Exit(0);
    }
    record Plan(string File,List<Entry> Entries);
    record Entry(string Handle,string Kind,string Source,string Target);
}
