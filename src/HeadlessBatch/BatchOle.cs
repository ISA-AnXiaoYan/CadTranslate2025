using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System.Text.Json;
public class BatchOle
{
 static JsonSerializerOptions Opt=new(){WriteIndented=true};
 [CommandMethod("CTBATCHOLEEXPORT",CommandFlags.Session)]public void Export()
 {
  var root=Environment.GetEnvironmentVariable("CADTRANS_WORK_ROOT")!;var folder=Path.Combine(root,"ole-v1");Directory.CreateDirectory(folder);var rows=new List<object>();int n=0;
  foreach(var file in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(root,"ole_inputs.json")))!)
  {
   using var db=new Database(false,true);db.ReadDwgFile(file,FileOpenMode.OpenForReadAndAllShare,false,null);using var tx=db.TransactionManager.StartTransaction();
   foreach(ObjectId bid in (BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead))foreach(ObjectId eid in (BlockTableRecord)tx.GetObject(bid,OpenMode.ForRead))if(tx.GetObject(eid,OpenMode.ForRead)is Ole2Frame ole)
   {string key="ole_"+(++n).ToString("D3");string dxf=Path.Combine(folder,key+".dxf");using var clone=db.Wblock(new ObjectIdCollection(new[]{eid}),Point3d.Origin);clone.DxfOut(dxf,16,DwgVersion.Current);rows.Add(new{Key=key,Handle=ole.Handle.ToString(),File=file});}
  }
  File.WriteAllText(Path.Combine(folder,"batch_objects.json"),JsonSerializer.Serialize(rows,Opt));Environment.Exit(0);
 }
 [CommandMethod("CTBATCHOLEAPPLY",CommandFlags.Session)]public void Apply()
 {
  var root=Environment.GetEnvironmentVariable("CADTRANS_WORK_ROOT")!;var folder=Path.Combine(root,"ole-v1");var objects=JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(Path.Combine(folder,"batch_objects.json")))!;var reports=new List<object>();
  if(objects.GroupBy(x=>x.File).GroupBy(g=>Path.GetFileName(g.Key),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new System.Exception("Duplicate basenames: use separate OLE work roots");
  foreach(var group in objects.GroupBy(x=>x.File))
  {
   string output=Path.Combine(folder,Path.GetFileName(group.Key));var errors=new List<string>();var done=new List<string>();
   try
   {
    using(var db=new Database(false,true))
    {
     db.ReadDwgFile(group.Key,FileOpenMode.OpenForReadAndAllShare,false,null);
     foreach(var row in group)
     {
      using var tx=db.TransactionManager.StartTransaction();var id=db.GetObjectId(false,new Handle(Convert.ToInt64(row.Handle,16)),0);var ole=(Ole2Frame)tx.GetObject(id,OpenMode.ForRead);var l=(LayerTableRecord)tx.GetObject(ole.LayerId,OpenMode.ForRead);bool locked=l.IsLocked;
      var f=new MemoryFiler();ole.DwgOut(f);int i=f.Data.FindIndex(p=>p.Value is byte[] b&&b.Length>128&&b.AsSpan(128,8).SequenceEqual(Convert.FromHexString("D0CF11E0A1B11AE1")));if(i<0)throw new System.Exception("Payload missing");
      var old=(byte[])f.Data[i].Value;var cfb=File.ReadAllBytes(Path.Combine(folder,"refined",row.Key+".ole"));var data=new byte[128+(cfb.Length+511)/512*512];Array.Copy(old,data,128);BitConverter.GetBytes((uint)(data.Length-128)).CopyTo(data,124);cfb.CopyTo(data,128);f.Data[i-1]=("UInt32",(uint)data.Length);f.Data[i]=("Bytes",data);
      if(locked){l.UpgradeOpen();l.IsLocked=false;}ole.UpgradeOpen();ole.DwgIn(f);if(locked)l.IsLocked=true;tx.Commit();done.Add(row.Key);
     }
     if(System.IO.File.Exists(output))throw new System.Exception("Output exists");db.SaveAs(output,true,DwgVersion.Current,db.SecurityParameters);
    }
    using(var db=new Database(false,true)){db.ReadDwgFile(output,FileOpenMode.OpenForReadAndAllShare,false,null);using var tx=db.TransactionManager.StartTransaction();foreach(var row in group){var id=db.GetObjectId(false,new Handle(Convert.ToInt64(row.Handle,16)),0);using var clone=db.Wblock(new ObjectIdCollection(new[]{id}),Point3d.Origin);clone.DxfOut(Path.Combine(folder,"native_verify_"+row.Key+".dxf"),16,DwgVersion.Current);}}
   }catch(System.Exception ex){errors.Add(ex.ToString());}
   reports.Add(new{File=group.Key,Output=output,Done=done,Errors=errors});
  }
  File.WriteAllText(Path.Combine(folder,"batch_apply_report.json"),JsonSerializer.Serialize(reports,Opt));Environment.Exit(0);
 }
 record Item(string Key,string Handle,string File);
}
