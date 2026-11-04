using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using System.Text.Json;
public class RepathImages
{
 record Job(string Input,string Output,List<Mapping> Paths);
 record Mapping(string Original,string Relative);
 [CommandMethod("CTREPATHIMAGES",CommandFlags.Session)]public void Run()
 {
  string root=Environment.GetEnvironmentVariable("CADTRANS_WORK_ROOT")!;var jobs=JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(Path.Combine(root,"image_repath_manifest.json")))!;var report=new List<object>();
  foreach(var job in jobs)
  {
   var errors=new List<string>();int changed=0;var expected=new Dictionary<string,string>();
   try
   {
    using(var db=new Database(false,true))
    {
     db.ReadDwgFile(job.Input,FileOpenMode.OpenForReadAndAllShare,false,null);
     using(var tr=db.TransactionManager.StartTransaction())
     {
      var seen=new HashSet<ObjectId>();
      foreach(ObjectId bid in (BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead))foreach(ObjectId eid in (BlockTableRecord)tr.GetObject(bid,OpenMode.ForRead))
      {
       if(tr.GetObject(eid,OpenMode.ForRead)is not RasterImage image||!seen.Add(image.ImageDefId))continue;
       var def=(RasterImageDef)tr.GetObject(image.ImageDefId,OpenMode.ForRead);var map=job.Paths.FirstOrDefault(x=>string.Equals(x.Original,def.SourceFileName,StringComparison.OrdinalIgnoreCase));
       if(map==null)throw new System.Exception("Unmapped image reference "+def.Handle);
       if(!System.IO.File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(job.Input)!,map.Relative))))throw new System.Exception("Staged image asset missing");
       def.UpgradeOpen();def.SourceFileName=map.Relative;expected[def.Handle.ToString()]=map.Relative;changed++;
      }
      tr.Commit();
     }
     Directory.CreateDirectory(Path.GetDirectoryName(job.Output)!);if(System.IO.File.Exists(job.Output))throw new System.Exception("Output exists");db.SaveAs(job.Output,true,DwgVersion.Current,db.SecurityParameters);
    }
    using(var db=new Database(false,true))
    {
     db.ReadDwgFile(job.Output,FileOpenMode.OpenForReadAndAllShare,false,null);using var tx=db.TransactionManager.StartTransaction();
     foreach(var p in expected){var def=(RasterImageDef)tx.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64(p.Key,16)),0),OpenMode.ForRead);if(def.SourceFileName!=p.Value)errors.Add("Path mismatch "+p.Key);if(!System.IO.File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(job.Output)!,p.Value))))errors.Add("Unresolved image "+p.Key);}
    }
   }catch(System.Exception ex){errors.Add(ex.ToString());}
   report.Add(new{job.Input,job.Output,RepathedDefinitions=changed,Errors=errors});
  }
  File.WriteAllText(Path.Combine(root,"image_repath_report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Environment.Exit(0);
 }
}
