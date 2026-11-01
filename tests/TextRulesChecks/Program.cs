static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
foreach(string value in new[]{"4-مكتب","2-مكتب","Office-1","WC-1","Patient-Room-2","Waiting-1"})
    Check(!TextRules.IsIdentifier(value),"Room name incorrectly protected: "+value);
foreach(string value in new[]{"FDB-01","AHU-02","CMW-15063-C001"})
{
    var values=new List<string>();string masked=TextRules.Mask(value,values);
    Check(masked=="__KEEP0__"&&values.SequenceEqual(new[]{value}),"Equipment code changed: "+value);
}
{
    var values=new List<string>();string masked=TextRules.Mask("4-مكتب",values);
    Check(masked=="__KEEP0__-مكتب"&&values.SequenceEqual(new[]{"4"}),"Arabic word was masked");
}
{
    var values=new List<string>();string masked=TextRules.Mask("Office-1/Cass-02",values);
    Check(masked.StartsWith("Office-__KEEP0__")&&values.SequenceEqual(new[]{"1","/Cass-02"}),"Room/equipment suffix boundaries changed");
}
{
    var values=new List<string>();string masked=TextRules.Mask("4C, 150mm² 250A",values);
    Check(values.Contains("150mm²")&&values.Contains("250")&&masked.Contains("__KEEP"),"Dimensions were not protected");
}
Console.WriteLine("TextRules regression checks passed");
