using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;
using System.Text.Json;
public class MemoryFiler:DwgFiler
{
 public List<(string Tag,object Value)> Data=new();public int Index;
 public override ErrorStatus FilerStatus{get;set;}=ErrorStatus.OK;
 public override FilerType FilerType=>FilerType.CopyFiler;
 public override long Position=>Index;
 public override void ResetFilerStatus(){FilerStatus=ErrorStatus.OK;}
 public override void Seek(long offset,int method){Index=method==0?(int)offset:Index+(int)offset;}
 T Read<T>(string tag){var p=Data[Index++];if(p.Tag!=tag)throw new System.Exception("Filer mismatch "+p.Tag+"/"+tag);return (T)p.Value;}
 public override ObjectId ReadHardOwnershipId()=>Read<ObjectId>("HardOwnershipId");
 public override void WriteHardOwnershipId(ObjectId value)=>Data.Add(("HardOwnershipId",value));
 public override ObjectId ReadSoftOwnershipId()=>Read<ObjectId>("SoftOwnershipId");
 public override void WriteSoftOwnershipId(ObjectId value)=>Data.Add(("SoftOwnershipId",value));
 public override ObjectId ReadHardPointerId()=>Read<ObjectId>("HardPointerId");
 public override void WriteHardPointerId(ObjectId value)=>Data.Add(("HardPointerId",value));
 public override ObjectId ReadSoftPointerId()=>Read<ObjectId>("SoftPointerId");
 public override void WriteSoftPointerId(ObjectId value)=>Data.Add(("SoftPointerId",value));
 public override string ReadString()=>Read<string>("String");
 public override void WriteString(string value)=>Data.Add(("String",value));
 public override Handle ReadHandle()=>Read<Handle>("Handle");
 public override void WriteHandle(Handle value)=>Data.Add(("Handle",value));
 public override long ReadInt64()=>Read<long>("Int64");
 public override void WriteInt64(long value)=>Data.Add(("Int64",value));
 public override int ReadInt32()=>Read<int>("Int32");
 public override void WriteInt32(int value)=>Data.Add(("Int32",value));
 public override short ReadInt16()=>Read<short>("Int16");
 public override void WriteInt16(short value)=>Data.Add(("Int16",value));
 public override ulong ReadUInt64()=>Read<ulong>("UInt64");
 public override void WriteUInt64(ulong value)=>Data.Add(("UInt64",value));
 public override uint ReadUInt32()=>Read<uint>("UInt32");
 public override void WriteUInt32(uint value)=>Data.Add(("UInt32",value));
 public override ushort ReadUInt16()=>Read<ushort>("UInt16");
 public override void WriteUInt16(ushort value)=>Data.Add(("UInt16",value));
 public override byte ReadByte()=>Read<byte>("Byte");
 public override void WriteByte(byte value)=>Data.Add(("Byte",value));
 public override bool ReadBoolean()=>Read<bool>("Boolean");
 public override void WriteBoolean(bool value)=>Data.Add(("Boolean",value));
 public override double ReadDouble()=>Read<double>("Double");
 public override void WriteDouble(double value)=>Data.Add(("Double",value));
 public override Point2d ReadPoint2d()=>Read<Point2d>("Point2d");
 public override void WritePoint2d(Point2d value)=>Data.Add(("Point2d",value));
 public override Point3d ReadPoint3d()=>Read<Point3d>("Point3d");
 public override void WritePoint3d(Point3d value)=>Data.Add(("Point3d",value));
 public override Vector2d ReadVector2d()=>Read<Vector2d>("Vector2d");
 public override void WriteVector2d(Vector2d value)=>Data.Add(("Vector2d",value));
 public override Vector3d ReadVector3d()=>Read<Vector3d>("Vector3d");
 public override void WriteVector3d(Vector3d value)=>Data.Add(("Vector3d",value));
 public override Scale3d ReadScale3d()=>Read<Scale3d>("Scale3d");
 public override void WriteScale3d(Scale3d value)=>Data.Add(("Scale3d",value));
 public override IntPtr ReadAddress()=>Read<IntPtr>("Address");
 public override void WriteAddress(IntPtr value)=>Data.Add(("Address",value));
 public override void WriteBytes(byte[] value)=>Data.Add(("Bytes",value.ToArray()));
 public override void ReadBytes(byte[] value){var data=Read<byte[]>("Bytes");if(data.Length!=value.Length)throw new System.Exception("Bytes size mismatch "+data.Length+"/"+value.Length);data.CopyTo(value,0);}
 public override byte[] ReadBinaryChunk()=>Read<byte[]>("BinaryChunk");
 public override void WriteBinaryChunk(byte[] value)=>Data.Add(("BinaryChunk",value.ToArray()));
}

