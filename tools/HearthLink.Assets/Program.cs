using HearthLink;
using System.Drawing.Imaging;
var folder=Path.GetFullPath(args.Length>0?args[0]:"assets"); Directory.CreateDirectory(folder);
int[] sizes=[16,20,24,32,40,48,64,128,256]; var images=new List<byte[]>();
foreach(int size in sizes)
{
    using var large=new Bitmap(1024,1024,PixelFormat.Format32bppArgb);
    using(var g=Graphics.FromImage(large)) Brand.DrawMark(g,new(0,0,1024,1024));
    using var bitmap=new Bitmap(size,size,PixelFormat.Format32bppArgb);
    using(var g=Graphics.FromImage(bitmap)) { g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(large,0,0,size,size); }
    using var data=new MemoryStream(); bitmap.Save(data,ImageFormat.Png); images.Add(data.ToArray());
    if(size==256) bitmap.Save(Path.Combine(folder,"HearthLink.png"),ImageFormat.Png);
}
using(var file=File.Create(Path.Combine(folder,"HearthLink.ico")))
using(var writer=new BinaryWriter(file))
{
    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length); int offset=6+16*sizes.Length;
    for(int i=0;i<sizes.Length;i++) { writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(images[i].Length); writer.Write(offset); offset+=images[i].Length; }
    foreach(var bytes in images) writer.Write(bytes);
}
Console.WriteLine("Created multi-resolution Windows icon (16–256 px) and PNG.");
