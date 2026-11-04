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
  if(args.Length is <1 or >4 || (args.Length>2 && (args.Length!=4 || args[1]!="--package")))
  {
   Console.Error.WriteLine("Usage: OleWorkbookWorker <disposable.ole> [active-plan.json | --package patched.xlsx all-sheet-plan.json]");
   Environment.ExitCode=2;
   return;
  }
  Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
  IStorage? storage=null;object? obj=null;
  dynamic? workbook=null;dynamic? application=null;
  bool closeOwned=false;
  bool? oldAlerts=null;
  try
  {
   Marshal.ThrowExceptionForHR(StgOpenStorage(args[0],IntPtr.Zero,0x12,IntPtr.Zero,0,out storage));
   bool packageMode=args.Length==4;
   if(packageMode)ReplacePackage(storage,args[2]);
   var iid=new Guid("00000000-0000-0000-C000-000000000046");
   int hr=OleLoad(storage,ref iid,IntPtr.Zero,out obj);Console.WriteLine("OleLoad="+hr.ToString("X8"));Marshal.ThrowExceptionForHR(hr);
   int run=OleRun(obj);Console.WriteLine("OleRun="+run.ToString("X8"));Marshal.ThrowExceptionForHR(run);
   dynamic wb=obj;dynamic app=wb.Application;
   workbook=wb;application=app;
   Console.WriteLine(JsonSerializer.Serialize(new{Name=(string)wb.Name,Sheets=(int)wb.Worksheets.Count,Visible=(bool)app.Visible,Version=(string)app.Version,ActiveSheet=(string)wb.ActiveSheet.Name}));
   if((bool)app.Visible)throw new InvalidOperationException("Refuse to use a visible Office server");
   closeOwned=true;
   if(args.Length>1){oldAlerts=(bool)app.DisplayAlerts;app.DisplayAlerts=false;}
   if(packageMode)
   {
    int checkedCells=VerifyPackage(wb,args[0],args[3]);
    wb.ActiveSheet.Calculate();
    Save(obj,storage);
    Console.WriteLine(JsonSerializer.Serialize(new{CheckedCells=checkedCells,Sheets=(int)wb.Worksheets.Count}));
   }
   else if(args.Length>1)
   {
    using var planDoc=JsonDocument.Parse(File.ReadAllText(args[1]));
    string name=Path.GetFileNameWithoutExtension(args[0]);
    var plan=planDoc.RootElement.EnumerateArray().Single(p=>p.GetProperty("File").GetString()==name);
    dynamic sheet=wb.ActiveSheet;dynamic used=sheet.UsedRange;
    object beforeFormula=used.Formula;object beforeValues=used.Value2;
    int changes=0;
    foreach(var entry in plan.GetProperty("Entries").EnumerateArray())
    {
     string source=entry.GetProperty("Source").GetString()!,target=entry.GetProperty("Target").GetString()!;
     if(source==target)continue;
     if(target.TrimStart().StartsWith('='))throw new InvalidOperationException("Refuse formula-like text in the active-sheet writer");
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
    Save(obj,storage);
    Console.WriteLine(JsonSerializer.Serialize(new{Object=name,Changes=changes,FormulaCount=formulaCount,NumericCount=numericCount,FormulaNumericDifferences=differences.Count}));
   }
  }
  catch(Exception ex){Console.WriteLine(ex.ToString());Environment.ExitCode=1;}
  finally
  {
   try
   {
    if(closeOwned)
    {
     if(oldAlerts.HasValue)application!.DisplayAlerts=oldAlerts.Value;
     workbook!.Close(false);
     if((int)application!.Workbooks.Count==0)application.Quit();
    }
   }
   catch(Exception ex){Console.Error.WriteLine(ex.Message);Environment.ExitCode=1;}
   finally
   {
    if(obj!=null&&Marshal.IsComObject(obj))Marshal.FinalReleaseComObject(obj);
    if(storage!=null)Marshal.FinalReleaseComObject(storage);
    OleUninitialize();
   }
  }
 }

 static void ReplacePackage(IStorage storage,string path)
 {
  storage.OpenStream("Package",IntPtr.Zero,0x12,0,out var stream);
  try
  {
   var bytes=File.ReadAllBytes(path);
   stream.SetSize(bytes.LongLength);
   stream.Seek(0,0,IntPtr.Zero);
   stream.Write(bytes,bytes.Length,IntPtr.Zero);
   stream.Commit(0);
  }
  finally{Marshal.FinalReleaseComObject(stream);}
  storage.Commit(0);
 }

 static int VerifyPackage(dynamic workbook,string olePath,string planPath)
 {
  using var doc=JsonDocument.Parse(File.ReadAllText(planPath));
  string key=Path.GetFileNameWithoutExtension(olePath);
  var plan=doc.RootElement.EnumerateArray().Single(p=>p.GetProperty("File").GetString()==key);
  int count=0;
  foreach(var group in plan.GetProperty("Entries").EnumerateArray().GroupBy(e=>e.GetProperty("Sheet").GetString()!))
  {
   dynamic used=workbook.Worksheets[group.Key].UsedRange;
   object matrix=used.Value2;
   int firstRow=(int)used.Row, firstColumn=(int)used.Column;
   foreach(var entry in group)
   {
    string address=entry.GetProperty("Handle").GetString()!;
    int offset=0,column=0;
    while(offset<address.Length && address[offset] is >= 'A' and <= 'Z')
     column=checked(column*26+address[offset++]-'A'+1);
    int row=int.Parse(address[offset..]);
    object? value=matrix is Array cells
     ? cells.GetValue(row-firstRow+cells.GetLowerBound(0),column-firstColumn+cells.GetLowerBound(1))
     : row==firstRow&&column==firstColumn?matrix:null;
    if(value is not string text || text!=entry.GetProperty("Target").GetString())
     throw new InvalidOperationException("Loaded text mismatch "+group.Key+"!"+address);
    count++;
   }
  }
  return count;
 }

 static void Save(object obj,IStorage storage)
 {
  ((IOleObject)obj).Update();
  Marshal.ThrowExceptionForHR(OleSave((IPersistStorage)obj,storage,true));
  ((IPersistStorage)obj).SaveCompleted(null);
  storage.Commit(0);
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

