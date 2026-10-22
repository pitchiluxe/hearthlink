namespace HearthLink.Core;
public static class CaptureSizing
{
    public static (int Width,int Height) Fit(int width,int height,int maxWidth,int maxHeight)
    {
        if(width<1 || height<1 || width>8192 || height>8192 || (long)width*height>34000000 || maxWidth<1 || maxHeight<1) throw new ArgumentOutOfRangeException(nameof(width));
        double scale=Math.Min(1,Math.Min((double)maxWidth/width,(double)maxHeight/height));
        return (Math.Max(1,(int)Math.Round(width*scale)),Math.Max(1,(int)Math.Round(height*scale)));
    }
}
