using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
public class Program
{
 [DllImport("ole32.dll")]static extern int OleInitialize(IntPtr reserved);
 [DllImport("ole32.dll")]static extern void OleUninitialize();
 [DllImport("ole32.dll",CharSet=CharSet.Unicode)]static extern int StgOpenStorage(string name,IntPtr priority,uint mode,IntPtr exclude,uint reserved,out IStorage storage);
 [DllImport("ole32.dll")]static extern int OleLoad(IStorage storage,ref Guid riid,IntPtr site,[MarshalAs(UnmanagedType.Interface)]out object obj);
 [DllImport("ole32.dll")]static extern int OleRun([MarshalAs(UnmanagedType.IUnknown)]object obj);
 [DllImport("ole32.dll")]static extern int OleSave(IPersistStorage obj,IStorage storage,[MarshalAs(UnmanagedType.Bool)]bool same);
 [STAThread]public static void Main(string[] args)
 {
  OleInitialize(IntPtr.Zero);IStorage? storage=null;object? obj=null;
  try
  {
   Marshal.ThrowExceptionForHR(StgOpenStorage(args[0],IntPtr.Zero,0x12,IntPtr.Zero,0,out storage));
   var iid=new Guid("00000000-0000-0000-C000-000000000046");
   int hr=OleLoad(storage,ref iid,IntPtr.Zero,out obj);Console.WriteLine("OleLoad="+hr.ToString("X8"));Marshal.ThrowExceptionForHR(hr);
   int run=OleRun(obj);Console.WriteLine("OleRun="+run.ToString("X8"));Marshal.ThrowExceptionForHR(run);
   dynamic wb=obj;dynamic app=wb.Application;
   Console.WriteLine(JsonSerializer.Serialize(new{Name=(string)wb.Name,Sheets=(int)wb.Worksheets.Count,Visible=(bool)app.Visible,Version=(string)app.Version,ActiveSheet=(string)wb.ActiveSheet.Name}));
   if(args.Length>1)
   {
    app.DisplayAlerts=false;
    var planDoc=JsonDocument.Parse(File.ReadAllText(args[1]));
    string name=Path.GetFileNameWithoutExtension(args[0]);
    var plan=planDoc.RootElement.EnumerateArray().Single(p=>p.GetProperty("File").GetString()==name);
    dynamic sheet=wb.ActiveSheet;dynamic used=sheet.UsedRange;
    object beforeFormula=used.Formula;object beforeValues=used.Value2;
    int changes=0;
    foreach(var entry in plan.GetProperty("Entries").EnumerateArray())
    {
     string source=entry.GetProperty("Source").GetString()!,target=entry.GetProperty("Target").GetString()!;
     if(source==target)continue;
     dynamic cell=sheet.Range[entry.GetProperty("Handle").GetString()!];
     if((bool)cell.HasFormula)throw new Exception("Refuse formula edit");
     if((string)cell.Value2!=source)throw new Exception("Cell source mismatch "+entry.GetProperty("Handle").GetString());
     cell.Value2=target;cell.Font.Name="Microsoft YaHei";changes++;
    }
    sheet.Calculate();
    object afterFormula=used.Formula;object afterValues=used.Value2;
    var differences=new List<string>();int formulaCount=0,numericCount=0;
    if(beforeFormula is Array bf&&afterFormula is Array af&&beforeValues is Array bv&&afterValues is Array av)
    {
     for(int r=bf.GetLowerBound(0);r<=bf.GetUpperBound(0);r++)for(int c=bf.GetLowerBound(1);c<=bf.GetUpperBound(1);c++)
     {
      var f=bf.GetValue(r,c);var v=bv.GetValue(r,c);
      if(f is string fs&&fs.StartsWith("=")){formulaCount++;if(!Equals(f,af.GetValue(r,c)))differences.Add($"formula:{r},{c}");}
      if(v is double n){numericCount++;var value=av.GetValue(r,c);if(value is not double n2||Math.Abs(n-n2)>1e-9*Math.Max(1,Math.Abs(n)))differences.Add($"number:{r},{c}");}
     }
    }
    if(differences.Count>0)throw new Exception("Formula/numeric drift: "+string.Join(",",differences.Take(8)));
    ((IOleObject)obj).Update();
    Marshal.ThrowExceptionForHR(OleSave((IPersistStorage)obj,storage,true));
    ((IPersistStorage)obj).SaveCompleted(null);storage.Commit(0);
    Console.WriteLine(JsonSerializer.Serialize(new{Object=name,Changes=changes,FormulaCount=formulaCount,NumericCount=numericCount,FormulaNumericDifferences=differences.Count}));
   }
   wb.Close(false);app.Quit();
  }
  catch(Exception ex){Console.WriteLine(ex.ToString());Environment.ExitCode=1;}
  finally{if(obj!=null&&Marshal.IsComObject(obj))Marshal.FinalReleaseComObject(obj);if(storage!=null)Marshal.FinalReleaseComObject(storage);OleUninitialize();}
 }
}
[ComImport,Guid("0000010A-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]public interface IPersistStorage
{
 void GetClassID(out Guid clsid);[PreserveSig]int IsDirty();void InitNew(IStorage storage);void Load(IStorage storage);void Save(IStorage storage,[MarshalAs(UnmanagedType.Bool)]bool same);void SaveCompleted(IStorage? storage);void HandsOffStorage();
}
[ComImport,Guid("00000112-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]public interface IOleObject
{
 void SetClientSite(IntPtr site);void GetClientSite(out IntPtr site);void SetHostNames([MarshalAs(UnmanagedType.LPWStr)]string app,[MarshalAs(UnmanagedType.LPWStr)]string obj);void Close(uint option);void SetMoniker(uint which,IntPtr moniker);void GetMoniker(uint assign,uint which,out IntPtr moniker);void InitFromData(IntPtr data,bool creation,uint reserved);void GetClipboardData(uint reserved,out IntPtr data);void DoVerb(int verb,IntPtr msg,IntPtr site,int index,IntPtr hwnd,IntPtr rect);void EnumVerbs(out IntPtr e);void Update();
}
[ComImport,Guid("0000000B-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]public interface IStorage
{
 void CreateStream([MarshalAs(UnmanagedType.LPWStr)]string name,uint mode,uint r1,uint r2,out IStream stream);
 void OpenStream([MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr reserved,uint mode,uint r2,out IStream stream);
 void CreateStorage([MarshalAs(UnmanagedType.LPWStr)]string name,uint mode,uint r1,uint r2,out IStorage storage);
 void OpenStorage([MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr priority,uint mode,IntPtr exclude,uint r,out IStorage storage);
 void CopyTo(uint count,IntPtr excludeIds,IntPtr excludeNames,IStorage dest);
 void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)]string name,IStorage dest,[MarshalAs(UnmanagedType.LPWStr)]string newName,uint flags);
 void Commit(uint flags);void Revert();void EnumElements(uint r1,IntPtr r2,uint r3,out IntPtr enumerator);
 void DestroyElement([MarshalAs(UnmanagedType.LPWStr)]string name);
 void RenameElement([MarshalAs(UnmanagedType.LPWStr)]string oldName,[MarshalAs(UnmanagedType.LPWStr)]string newName);
 void SetElementTimes([MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr c,IntPtr a,IntPtr m);
 void SetClass(ref Guid clsid);void SetStateBits(uint bits,uint mask);void Stat(out STATSTG stat,uint flags);
}

