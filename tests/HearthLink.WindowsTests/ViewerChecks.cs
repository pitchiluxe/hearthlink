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
        var viewer=await window.OnUi(()=> { var v=new Viewer(viewerEnd,"minimized",false,["1: test"],initialQuality:3) {ShowInTaskbar=false,WindowState=FormWindowState.Minimized}; v.Show(); return v; });
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var acked=new List<long>();
        try
        {
            var initial=await hostEnd.Read(limit.Token);
            check(initial.Kind=="profile" && initial.Value==3,"saved quality is sent to the host when a new viewer starts");
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

        var (defaultEnd,defaultHost)=await FileChecks.Linked(host,controller);
        var defaultViewer=await window.OnUi(()=> {var v=new Viewer(defaultEnd,"default quality",false,["1: test"]) {ShowInTaskbar=false,WindowState=FormWindowState.Minimized}; v.Show(); return v;});
        try
        {
            check(await window.OnUi(()=>!defaultViewer.Controls.OfType<FlowLayoutPanel>().SelectMany(p=>p.Controls.OfType<Button>()).Single(b=>b.Name=="SessionCommands").Enabled),"view-only and older hosts have no enabled remote command menu");
            var defaultProfile=await defaultHost.Read(limit.Token);
            check(defaultProfile.Kind=="profile" && defaultProfile.Value==2,"a default viewer requests Full detail from the remote host");
        }
        finally {await window.OnUi(()=> {defaultViewer.Close(); return true;}); defaultHost.Dispose();}

        var (commandEnd,commandHost)=await FileChecks.Linked(host,controller);
        var commandViewer=await window.OnUi(()=> {var v=new Viewer(commandEnd,"commands",true,["test"],initialQuality:0,supportsCommands:true); v.Show(); return v;});
        try
        {
            var state=await window.OnUi(()=>
            {
                var button=commandViewer.Controls.OfType<FlowLayoutPanel>().SelectMany(p=>p.Controls.OfType<Button>()).Single(b=>b.Name=="SessionCommands");
                var menu=(ContextMenuStrip)button.Tag!;
                return (button.Enabled,Menu:menu,Thread:NativeDialogs.CurrentThread());
            });
            check(state.Enabled && state.Menu.Items.Count==4 && !state.Menu.Items[0].Enabled,"control menu exposes power actions and explains unavailable Ctrl+Alt+Delete");
            foreach(var (index,command) in new[] {(1,"restart"),(2,"signout"),(3,"shutdown")})
            {
                var answer=NativeDialogs.Answer(state.Thread,NativeDialogs.Yes,TimeSpan.FromSeconds(5));
                await window.OnUi(()=> {state.Menu.Items[index].PerformClick(); return true;});
                check((await answer).Contains("connected PC"),"remote "+command+" requires explicit confirmation");
                Packet packet;
                do {packet=await commandHost.Read(limit.Token);} while(packet.Kind!="session-command");
                check(packet.Text==command,"confirmed "+command+" is sent only to the connected peer");
            }
            await commandHost.Send(new("command-result",Text:"Windows could not complete the command."),limit.Token);
            await commandHost.Send(new("frame",Frame:new(Jpeg(1),64,48,0,1,100,100,10)),limit.Token);
            Packet feedback;
            do {feedback=await commandHost.Read(limit.Token);} while(feedback.Kind!="presented");
            check(feedback.Sequence==1 && await window.OnUi(()=>commandViewer.Controls.OfType<Label>().Any(l=>l.Visible && l.Text.Contains("could not complete"))),"command failure stays visible while video acknowledgements continue");
            var reject=NativeDialogs.Answer(state.Thread,NativeDialogs.No,TimeSpan.FromSeconds(5));
            await window.OnUi(()=> {state.Menu.Items[3].PerformClick(); return true;}); await reject;
            using var silence=new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            bool sent=false;
            try {while(true) {if((await commandHost.Read(silence.Token)).Kind=="session-command") {sent=true; break;}}} catch(OperationCanceledException) { }
            check(!sent,"declining the shutdown confirmation sends no power command");
        }
        finally {await window.OnUi(()=> {commandViewer.Close(); return true;}); commandHost.Dispose();}

        bool ended=false;
        // Do not depend on or move the user's actual pointer: hovering intentionally keeps the notice open.
        var shown=await window.OnUi(()=> { var n=new SessionNotice("TECHNOBIZ-ASUS",true,()=>ended=true,TimeSpan.FromMilliseconds(300),()=>Point.Empty); n.Show(); return (Notice:n,Full:!n.Collapsed && n.Height>100); });
        var notice=shown.Notice; bool fullAtStart=shown.Full;
        var wait=System.Diagnostics.Stopwatch.StartNew();
        while(wait.Elapsed<TimeSpan.FromSeconds(5) && !await window.OnUi(()=>notice.Collapsed)) await Task.Delay(100);
        var collapsed=await window.OnUi(()=>(notice.Collapsed,notice.Visible,notice.TopMost,notice.Height,notice.Bounds));
        var area=Screen.PrimaryScreen!.WorkingArea;
        check(fullAtStart && collapsed.Collapsed && collapsed.Visible && collapsed.TopMost && collapsed.Height<=40 && area.Contains(collapsed.Bounds),"session notice collapses to a small tab that stays on screen and on top");
        bool expanded=await window.OnUi(()=> { notice.Expand(); return !notice.Collapsed && notice.Height>100; });
        await window.OnUi(()=> { notice.EndButton.PerformClick(); notice.CloseAfterSession(); return true; });
        check(expanded && ended,"the collapsed tab expands back to End session");
    }
}
