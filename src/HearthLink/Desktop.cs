using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HearthLink.Core;

namespace HearthLink;
public static class Desktop
{
    public static byte[] Capture(int monitor)
    {
        var screens=Screen.AllScreens;
        if(monitor<0 || monitor>=screens.Length) throw new InvalidDataException("Display no longer available.");
        var bounds=screens[monitor].Bounds;
        if((long)bounds.Width*bounds.Height>34000000) throw new InvalidDataException("Display exceeds capture limit.");
        using var bitmap=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format24bppRgb);
        using(var g=Graphics.FromImage(bitmap)) g.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);
        using var output=new MemoryStream();
        var codec=ImageCodecInfo.GetImageEncoders().First(c=>c.FormatID==ImageFormat.Jpeg.Guid);
        using var parameters=new EncoderParameters(1);
        parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,55L);
        bitmap.Save(output,codec,parameters); return output.ToArray();
    }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION data; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSE mouse; [FieldOffset(0)] public KEY key; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSE { public int x,y; public uint data,flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEY { public ushort vk,scan; public uint flags,time; public UIntPtr extra; }
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,INPUT[] inputs,int size);
    public static void Input(Packet p,int monitor)
    {
        var screens=Screen.AllScreens;
        if(monitor<0 || monitor>=screens.Length) return;
        INPUT input=new();
        if(p.Kind=="key")
        {
            if(p.Value<1 || p.Value>254) return;
            input.type=1; input.data.key.vk=(ushort)p.Value; input.data.key.flags=p.Down?0u:2u;
            if(p.Value is 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 45 or 46 or 91 or 92 or 163 or 165) input.data.key.flags|=1;
        }
        else if(p.Kind=="move")
        {
            var b=screens[monitor].Bounds; var v=SystemInformation.VirtualScreen;
            int x=b.X+(int)((long)Math.Clamp(p.X,0,65535)*(b.Width-1)/65535);
            int y=b.Y+(int)((long)Math.Clamp(p.Y,0,65535)*(b.Height-1)/65535);
            input.data.mouse.x=(int)((long)(x-v.X)*65535/Math.Max(1,v.Width-1));
            input.data.mouse.y=(int)((long)(y-v.Y)*65535/Math.Max(1,v.Height-1));
            input.data.mouse.flags=0x8000|0x4000|1;
        }
        else if(p.Kind=="button") input.data.mouse.flags=p.Value switch {1=>p.Down?2u:4u,2=>p.Down?8u:16u,3=>p.Down?32u:64u,_=>0};
        else if(p.Kind=="wheel") { input.data.mouse.flags=0x800; input.data.mouse.data=unchecked((uint)Math.Clamp(p.Value,-1200,1200)); }
        else return;
        if(SendInput(1,[input],Marshal.SizeOf<INPUT>())!=1) throw new InvalidOperationException("Windows blocked input; elevated and protected screens are unsupported.");
    }
}
