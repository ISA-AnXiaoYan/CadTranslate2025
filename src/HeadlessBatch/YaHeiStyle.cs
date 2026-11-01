using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class YaHeiStyle
{
    public const string Name="CADTRANS_微软雅黑";
    public static ObjectId Ensure(Database db,Transaction tr)
    {
        var table=(TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead);
        TextStyleTableRecord style;ObjectId id;
        if(table.Has(Name)){id=table[Name];style=(TextStyleTableRecord)tr.GetObject(id,OpenMode.ForWrite);}
        else{table.UpgradeOpen();style=new TextStyleTableRecord{Name=Name};id=table.Add(style);tr.AddNewlyCreatedDBObject(style,true);}
        style.FileName=@"C:\Windows\Fonts\msyh.ttc";
        style.BigFontFileName="";
        style.Font=new FontDescriptor("Microsoft YaHei",false,false,134,34);
        style.TextSize=0;style.XScale=1;style.ObliquingAngle=0;
        db.Textstyle=id;
        return id;
    }
    public static string Contents(string value)=>"\\fMicrosoft YaHei|b0|i0|c134|p34;"+Regex.Replace(value,@"\\[fF][^;]*;","");
}

public class FontFix
{
    static object Describe(TextStyleTableRecord s)=>new{Name=s.Name,FileName=s.FileName,Typeface=s.Font.TypeFace,Charset=s.Font.CharacterSet,BigFont=s.BigFontFileName,Shape=s.IsShapeFile};
    static string Text(Entity e)=>e switch{DBText d=>d.TextString,MText m=>m.Text,MLeader l=>l.MText.Text,_=>""};
    static ObjectId Style(Entity e)=>e switch{DBText d=>d.TextStyleId,MText m=>m.TextStyleId,MLeader l=>l.MText.TextStyleId,_=>ObjectId.Null};
    [CommandMethod("CT1101FONTFIX",CommandFlags.Session)]
    public void Run()
    {
        string root=Environment.GetEnvironmentVariable("CADTRANS_ROOT")!;
        var plans=JsonSerializer.Deserialize<List<Plan>>(File.ReadAllText(Path.Combine(root,"preview_plan.json")))!;
        var reports=new List<object>();var fixRoot=Path.Combine(root,Environment.GetEnvironmentVariable("CADTRANS_FONTFIX")??"fontfix-v1");
        foreach(var p in plans)
        {
            string rel=Path.Combine(Path.GetDirectoryName(p.File)!,Path.GetFileNameWithoutExtension(p.File)+"_ZH.dwg");
            string input=Path.Combine(fixRoot,"before",rel),output=Path.Combine(fixRoot,"after",rel);
            var oldStyles=new Dictionary<string,object>();var texts=new Dictionary<string,string>();var layerLocks=new Dictionary<string,bool>();
            var errors=new List<string>();object? newStyle=null;
            try
            {
                using(var db=new Database(false,true))
                {
                    db.ReadDwgFile(input,FileOpenMode.OpenForReadAndAllShare,false,null);
                    var prev=HostApplicationServices.WorkingDatabase;
                    try
                    {
                        HostApplicationServices.WorkingDatabase=db;
                        using(var tx=db.TransactionManager.StartTransaction())
                        {
                            var styleId=YaHeiStyle.Ensure(db,tx);
                            foreach(ObjectId lid in (LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForRead)){var l=(LayerTableRecord)tx.GetObject(lid,OpenMode.ForRead);layerLocks[l.Name]=l.IsLocked;}
                            foreach(var item in p.Entries.Where(x=>x.Target!=x.Source))
                            {
                                var id=db.GetObjectId(false,new Handle(Convert.ToInt64(item.Handle,16)),0);
                                var e=(Entity)tx.GetObject(id,OpenMode.ForRead);
                                texts[item.Handle]=Text(e);
                                var old=(TextStyleTableRecord)tx.GetObject(Style(e),OpenMode.ForRead);oldStyles[old.Name]=Describe(old);
                                var layer=(LayerTableRecord)tx.GetObject(e.LayerId,OpenMode.ForRead);bool locked=layer.IsLocked;
                                if(locked){layer.UpgradeOpen();layer.IsLocked=false;}
                                e.UpgradeOpen();
                                switch(e){case DBText d:d.TextStyleId=styleId;break;case MText m:m.TextStyleId=styleId;m.Contents=YaHeiStyle.Contents(m.Contents);break;case MLeader l:using(var m=l.MText){m.TextStyleId=styleId;m.Contents=YaHeiStyle.Contents(m.Contents);l.MText=m;}break;}
                                if(locked)layer.IsLocked=true;
                            }
                            tx.Commit();
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        if(File.Exists(output))throw new System.Exception("Refuse to overwrite repair output");
                        db.SaveAs(output,true,DwgVersion.Current,db.SecurityParameters);
                    }
                    finally{HostApplicationServices.WorkingDatabase=prev;}
                }
                using(var db=new Database(false,true))
                {
                    db.ReadDwgFile(output,FileOpenMode.OpenForReadAndAllShare,false,null);
                    using var tx=db.TransactionManager.StartTransaction();
                    var table=(TextStyleTable)tx.GetObject(db.TextStyleTableId,OpenMode.ForRead);
                    var sid=table[YaHeiStyle.Name];var style=(TextStyleTableRecord)tx.GetObject(sid,OpenMode.ForRead);newStyle=Describe(style);
                    if((style.Font.TypeFace!="Microsoft YaHei"&&style.Font.TypeFace!="微软雅黑")||style.Font.CharacterSet!=134||style.IsShapeFile||!string.IsNullOrEmpty(style.BigFontFileName))errors.Add("Font descriptor mismatch");
                    foreach(var pair in texts){var e=(Entity)tx.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64(pair.Key,16)),0),OpenMode.ForRead);if(Style(e)!=sid)errors.Add("Style mismatch "+pair.Key);if(Text(e)!=pair.Value)errors.Add("Text changed "+pair.Key);if(e is MText mt&&Regex.Matches(mt.Contents,@"\\[fF][^;]*;").Cast<Match>().Any(m=>!m.Value.Contains("Microsoft YaHei")))errors.Add("Inline override "+pair.Key);}
                    foreach(ObjectId lid in (LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForRead)){var l=(LayerTableRecord)tx.GetObject(lid,OpenMode.ForRead);if(layerLocks[l.Name]!=l.IsLocked)errors.Add("Layer lock changed "+l.Name);}
                }
            }
            catch(System.Exception ex){errors.Add(ex.ToString());}
            reports.Add(new{File=p.File,Output=output,StyleAssignments=texts.Count,PreviousStyles=oldStyles.Values,NewStyle=newStyle,Errors=errors});
        }
        File.WriteAllText(Path.Combine(fixRoot,"report.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        Environment.Exit(0);
    }
    record Plan(string File,List<Entry> Entries);
    record Entry(string Handle,string Source,string Target);
}
