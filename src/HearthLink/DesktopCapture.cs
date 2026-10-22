using HearthLink.Core;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HearthLink;
/// <summary>Reusable native capture surface; only JPEG bytes are allocated per frame.</summary>
public sealed class DesktopCapture : IDisposable
{
    static readonly ImageCodecInfo Jpeg=ImageCodecInfo.GetImageEncoders().First(c=>c.FormatID==ImageFormat.Jpeg.Guid);
    readonly MemoryStream encoded=new();
    readonly EncoderParameters parameters=new(1);
    Bitmap? surface;
    Graphics? graphics;
    int quality=-1;
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll",SetLastError=true)] static extern bool StretchBlt(IntPtr target,int x,int y,int width,int height,IntPtr source,int sourceX,int sourceY,int sourceWidth,int sourceHeight,uint operation);
    [DllImport("gdi32.dll",SetLastError=true)] static extern bool BitBlt(IntPtr target,int x,int y,int width,int height,IntPtr source,int sourceX,int sourceY,uint operation);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc,int mode);
    public static (int Width,int Height,int Quality,int Fps) Profile(int profile)=>profile switch {1=>(1600,900,65,45),2=>(8192,8192,75,30),_=>(1280,720,55,60)};
    public VideoFrame Capture(int monitor,long sequence,int profile=0,int roundTripMilliseconds=0)
    {
        var started=Stopwatch.GetTimestamp(); var displays=Screen.AllScreens;
        if(monitor<0 || monitor>=displays.Length) throw new InvalidDataException("Display no longer available.");
        var bounds=displays[monitor].Bounds; var settings=Profile(profile);
        var size=CaptureSizing.Fit(bounds.Width,bounds.Height,settings.Width,settings.Height);
        if(surface==null || surface.Width!=size.Width || surface.Height!=size.Height)
        {
            graphics?.Dispose(); surface?.Dispose(); surface=new(size.Width,size.Height,PixelFormat.Format24bppRgb); graphics=Graphics.FromImage(surface);
        }
        IntPtr source=GetDC(IntPtr.Zero);
        if(source==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Desktop capture unavailable.");
        IntPtr target=IntPtr.Zero;
        try
        {
            target=graphics!.GetHdc();
            bool copied;
            if(size.Width==bounds.Width && size.Height==bounds.Height) copied=BitBlt(target,0,0,size.Width,size.Height,source,bounds.X,bounds.Y,0x40CC0020);
            else {SetStretchBltMode(target,3); copied=StretchBlt(target,0,0,size.Width,size.Height,source,bounds.X,bounds.Y,bounds.Width,bounds.Height,0x00CC0020);}
            if(!copied) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Windows blocked desktop capture.");
        }
        finally {if(target!=IntPtr.Zero) graphics!.ReleaseHdc(target); ReleaseDC(IntPtr.Zero,source);}
        int capture=(int)Math.Min(int.MaxValue,Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        var encoding=Stopwatch.GetTimestamp();
        if(quality!=settings.Quality)
        {
            if(quality!=-1) parameters.Param[0].Dispose();
            parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,(long)settings.Quality); quality=settings.Quality;
        }
        encoded.SetLength(0); encoded.Position=0; surface.Save(encoded,Jpeg,parameters);
        int encode=(int)Math.Min(int.MaxValue,Stopwatch.GetElapsedTime(encoding).TotalMicroseconds);
        return new(encoded.ToArray(),size.Width,size.Height,monitor,sequence,capture,encode,roundTripMilliseconds);
    }
    public void Dispose() {graphics?.Dispose(); surface?.Dispose(); encoded.Dispose(); parameters.Dispose();}
}
