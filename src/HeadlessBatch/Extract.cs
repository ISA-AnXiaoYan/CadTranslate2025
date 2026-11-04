using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using System.Text.Json;
using System.Text.RegularExpressions;

public class Extract
{
    [CommandMethod("CADTRANS_HEADLESS_SCAN", CommandFlags.Session)]
    public void Run()
    {
        var root = Environment.GetEnvironmentVariable("CADTRANS_WORK_ROOT")!;
        var inputs = Directory.GetFiles(Path.Combine(root,"staging"),"*.dwg",SearchOption.AllDirectories);
        var reports = new List<object>();
        foreach (var path in inputs)
        {
            var rows = new List<object>(); var issues = new List<string>();
            var references = new List<object>();
            var protectedContent = new List<object>();
            int xrefs=0, tables=0, proxies=0;
            var types=new Dictionary<string,int>(); var reachable=new HashSet<ObjectId>();
            try
            {
                using var db = new Database(false,true);
                db.ReadDwgFile(path,FileOpenMode.OpenForReadAndAllShare,false,null);
                using var tx = db.TransactionManager.StartTransaction();
                var layerLocks=new Dictionary<string,bool>();
                foreach(ObjectId layerId in (LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForRead))
                {
                    var layer=(LayerTableRecord)tx.GetObject(layerId,OpenMode.ForRead);
                    layerLocks[layer.Name]=layer.IsLocked;
                }
                void Visit(ObjectId bid)
                {
                    if(!reachable.Add(bid))return;
                    var block=(BlockTableRecord)tx.GetObject(bid,OpenMode.ForRead);
                    if(block.IsFromExternalReference||block.IsDependent)return;
                    foreach(ObjectId eid in block)if(tx.GetObject(eid,OpenMode.ForRead) is BlockReference b)Visit(b.BlockTableRecord);
                }
                foreach(ObjectId bid in (BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead))
                    if(((BlockTableRecord)tx.GetObject(bid,OpenMode.ForRead)).IsLayout)Visit(bid);
                var seen = new HashSet<string>();
                void Add(Entity e, string text, string location, int? row = null, int? column = null)
                {
                    if(string.IsNullOrWhiteSpace(text))return;
                    var plain=Regex.Replace(text.Replace("\\P"," "),@"\\[HhWwCcFfTtQqAa][^;{}]*;|[{}]","");
                    plain=Regex.Replace(plain,@"\s+"," ").Trim();
                    var pos = e switch { DBText d => d.Position, MText m => m.Location, _ => Autodesk.AutoCAD.Geometry.Point3d.Origin };
                    double? minX=null,minY=null,maxX=null,maxY=null;
                    try { var ex=e.GeometricExtents;minX=ex.MinPoint.X;minY=ex.MinPoint.Y;maxX=ex.MaxPoint.X;maxY=ex.MaxPoint.Y; } catch { }
                    var owner=tx.GetObject(e.OwnerId,OpenMode.ForRead);
                    bool used=reachable.Contains(e.OwnerId)||(owner is BlockReference brOwner && reachable.Contains(brOwner.OwnerId));
                    bool eligible=e.Visible && (e is not AttributeDefinition ad || (ad.Constant && !ad.Invisible)) && (e is not AttributeReference ar || !ar.Invisible);
                    rows.Add(new {Handle=e.Handle.ToString(),Kind=e.GetType().Name,Layer=e.Layer,Location=location,Row=row,Column=column,Raw=text,Text=plain,Reachable=used,Visible=eligible,X=pos.X,Y=pos.Y,MinX=minX,MinY=minY,MaxX=maxX,MaxY=maxY});
                }
                void Read(ObjectId id,string location)
                {
                    if(!id.IsValid || !seen.Add(id.Handle.ToString()))return;
                    try
                    {
                        if(tx.GetObject(id,OpenMode.ForRead) is not Entity e)return;
                        if(reachable.Contains(e.OwnerId)){var name=e.GetType().Name;types[name]=types.GetValueOrDefault(name)+1;}
                        switch(e)
                        {
                            case DBText d: Add(e,d.TextString,location);break;
                            case MText m: Add(e,m.Contents,location);break;
                            case MLeader l when l.ContentType==ContentType.MTextContent:
                                using(var m=l.MText){if(m!=null)Add(e,m.Contents,location);}break;
                            case Table t:
                                tables++;
                                for(int r=0;r<t.Rows.Count;r++)
                                for(int c=0;c<t.Columns.Count;c++)
                                {
                                    var cell=t.Cells[r,c];
                                    if(cell.Contents.Count==0)continue;
                                    var content=cell.Contents[0];
                                    if(cell.Contents.Count==1 && !content.HasFormula && content.FieldId.IsNull && content.BlockTableRecordId.IsNull)
                                        Add(e,content.TextString,location+$"/cell:{r},{c}",r,c);
                                    else if(reachable.Contains(e.OwnerId))
                                        protectedContent.Add(new{Handle=e.Handle.ToString(),Kind="ProtectedTableCell",Row=r,Column=c,Count=cell.Contents.Count});
                                }
                                break;
                            case MLeader leader when leader.ContentType==ContentType.BlockContent && reachable.Contains(e.OwnerId):
                                if(!leader.BlockContentId.IsNull)
                                {
                                    var block=(BlockTableRecord)tx.GetObject(leader.BlockContentId,OpenMode.ForRead);
                                    foreach(ObjectId child in block)
                                    {
                                        var definition=tx.GetObject(child,OpenMode.ForRead);
                                        if(definition is AttributeDefinition)
                                        {
                                            using var attribute=leader.GetBlockAttribute(child);
                                            if(attribute!=null)protectedContent.Add(new{Handle=e.Handle.ToString(),Kind="LeaderBlockAttribute",Definition=child.Handle.ToString(),Text=attribute.TextString});
                                        }
                                        else if(definition is DBText text)
                                            protectedContent.Add(new{Handle=e.Handle.ToString(),Kind="LeaderBlockText",Definition=child.Handle.ToString(),Text=text.TextString});
                                        else if(definition is MText blockText)
                                            protectedContent.Add(new{Handle=e.Handle.ToString(),Kind="LeaderBlockText",Definition=child.Handle.ToString(),Text=blockText.Contents});
                                    }
                                }
                                break;
                            case Dimension dim when reachable.Contains(e.OwnerId) && !string.IsNullOrEmpty(dim.DimensionText):
                                protectedContent.Add(new{Handle=e.Handle.ToString(),Kind="DimensionOverride",Text=dim.DimensionText});
                                break;
                            case RasterImage image when reachable.Contains(e.OwnerId):
                                var def=image.ImageDefId.IsNull?null:tx.GetObject(image.ImageDefId,OpenMode.ForRead) as RasterImageDef;
                                references.Add(new{Handle=e.Handle.ToString(),Kind="RasterImage",SourcePath=def?.SourceFileName});
                                break;
                        }
                        if(e.GetType().Name.Contains("Proxy"))proxies++;
                        if(e is BlockReference br)foreach(ObjectId aid in br.AttributeCollection)Read(aid,location+"/attribute");
                    }
                    catch(System.Exception ex){issues.Add(id.Handle+":"+ex.Message);}
                }
                foreach(ObjectId bid in (BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead))
                {
                    var b=(BlockTableRecord)tx.GetObject(bid,OpenMode.ForRead);
                    if(b.IsFromExternalReference||b.IsDependent){xrefs++;continue;}
                    foreach(ObjectId eid in b)Read(eid,b.Name);
                }
                reports.Add(new {File=Path.GetRelativePath(Path.Combine(root,"staging"),path),Rows=rows,Issues=issues,XrefBlocks=xrefs,Tables=tables,Proxies=proxies,EntityTypes=types,LayerLocks=layerLocks,References=references,ProtectedContent=protectedContent});
            }
            catch(System.Exception ex){reports.Add(new {File=path,Error=ex.ToString()});}
        }
        File.WriteAllText(Path.Combine(root,"preview_extract.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
        // All side databases have been disposed and evidence synchronously written.
        // This isolated process must not save its staging host on QUIT.
        Environment.Exit(0);
    }
}
