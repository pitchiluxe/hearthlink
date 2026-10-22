using HearthLink.Core;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
namespace HearthLink;
public static class FrameDecoder
{
    public static Bitmap Decode(VideoFrame frame)
    {
        using var stream=new MemoryStream(frame.Jpeg,false);
        using var image=Image.FromStream(stream,false,false);
        if(image.Width!=frame.Width || image.Height!=frame.Height) throw new InvalidDataException("Decoded frame dimensions do not match.");
        var bitmap=new Bitmap(frame.Width,frame.Height,PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics=Graphics.FromImage(bitmap); graphics.CompositingMode=CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(image,0,0); return bitmap;
        }
        catch {bitmap.Dispose(); throw;}
    }
}
