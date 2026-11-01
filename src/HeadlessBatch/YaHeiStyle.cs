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
