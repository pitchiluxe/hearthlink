using HearthLink;
using HearthLink.Core;
using System.Drawing.Imaging;

// The real Viewer against a hand-driven host end, on the fixture's UI thread.
static class ViewerChecks
{
    static byte[] Jpeg(int seed)
    {
        using var bitmap=new Bitmap(64,48); using(var g=Graphics.FromImage(bitmap)) g.Clear(Color.FromArgb(seed*40%255,90,160));
        using var stream=new MemoryStream(); bitmap.Save(stream,ImageFormat.Jpeg); return stream.ToArray();
    }
    public static async Task Run(MainWindow window,IdentityStore host,IdentityStore controller,Action<bool,string> check)
    {
        var (viewerEnd,hostEnd)=await FileChecks.Linked(host,controller);
        // A minimized (or fully covered) window gets no paint messages; the session must still flow.
        var viewer=await window.OnUi(()=> { var v=new Viewer(viewerEnd,"minimized",false,["1: test"]) {ShowInTaskbar=false,WindowState=FormWindowState.Minimized}; v.Show(); return v; });
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var acked=new List<long>();
        try
        {
            for(long sequence=1;sequence<=3;sequence++)
            {
                await hostEnd.Send(new("frame",Frame:new(Jpeg((int)sequence),64,48,0,sequence,100,100,10)),limit.Token);
                while(true) { var p=await hostEnd.Read(limit.Token); if(p.Kind=="presented") { acked.Add(p.Sequence); break; } }
            }
        }
        catch(OperationCanceledException) { }
        await window.OnUi(()=> { viewer.Close(); return true; });
        hostEnd.Dispose();
        check(acked.SequenceEqual([1L,2L,3L]),"a minimized viewer still acknowledges every frame");
    }
}
