using HearthLink.Core;
using System.Diagnostics;

namespace HearthLink;
public sealed class Viewer : Form
{
    readonly Connection connection;
    sealed class RemoteScreen : PictureBox
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal long Sequence {get;set;}
        long presented;
        public event Action<long>? Presented;
        public RemoteScreen() {SetStyle(ControlStyles.Selectable|ControlStyles.OptimizedDoubleBuffer,true); TabStop=true;}
        protected override bool IsInputKey(Keys keyData)=>true;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if(Sequence>presented && Image!=null) {presented=Sequence; Presented?.Invoke(presented);}
        }
    }
    readonly RemoteScreen screen=new() {Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.Black};
    readonly Label status=new() {ForeColor=Color.White,AutoEllipsis=true,Width=310,Height=38,Padding=new(8)};
    readonly CancellationTokenSource stop=new();
    readonly InputMailbox outbound=new();
    readonly bool control;
    readonly Stopwatch counter=Stopwatch.StartNew();
    int desiredMonitor,paintedFrames;
    VideoFrame? current;
    bool full;
    Rectangle previous;
    // The toolbar floats over the remote screen and stays hidden, so only the connected PC is visible.
    readonly FlowLayoutPanel bar=new() {AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new(12,10,12,8),BackColor=Brand.Navy,WrapContents=false,Visible=false};
    readonly System.Windows.Forms.Timer barTimer=new() {Interval=150};
    readonly Stopwatch barIdle=new();
    TimeSpan barHold;
    // Open while a file dialog or browser is up, so the toolbar does not slide away under it.
    bool holdBar, menuOpen;
    TransferStrip? transfers;
    RemoteFilesWindow? remoteFiles;
    public Viewer(Connection c,string name,bool allowControl,string[] monitors,string route="relay",Func<CancellationToken,Task<Connection>>? openFiles=null)
    {
        connection=c; control=allowControl;
        Text="HearthLink • "+name; Width=1280; Height=800; MinimumSize=new(640,400); KeyPreview=true; Icon=Brand.AppIcon; Font=new("Segoe UI",10);
        // Menu buttons, as in TeamViewer's toolbar: a menu pops up above everything, where combo box lists failed to open on some PCs.
        var displays=Choice(monitors,0,index=> {Volatile.Write(ref desiredMonitor,index); Post(new("monitor",Value:index));});
        var profile=Choice(DesktopCapture.Profiles,0,index=>Post(new("profile",Value:index)));
        var fullscreen=Brand.Style(new Button {Text="Full screen",AutoSize=true}); fullscreen.Click+=(_,_)=>ToggleFull();
        var disconnect=Brand.Style(new Button {Text="Disconnect",AutoSize=true},true); disconnect.Click+=(_,_)=>Close();
        status.Text=(control?"Connected • Control":"Connected • View only")+" • "+route;
        bar.Controls.AddRange([displays,profile]);
        if(control && openFiles!=null)
        {
            var strip=transfers=new TransferStrip(openFiles);
            var send=Brand.Style(new Button {Text="Send files",AutoSize=true});
            send.Click+=async(_,_)=>
            {
                using var pick=new OpenFileDialog {Multiselect=true,Title="Send files to "+name};
                holdBar=true; bool chosen=pick.ShowDialog(this)==DialogResult.OK; holdBar=false;
                if(chosen) await strip.Send(pick.FileNames);
            };
            var get=Brand.Style(new Button {Text="Get files",AutoSize=true});
            get.Click+=(_,_)=>
            {
                if(remoteFiles is {IsDisposed:false}) { remoteFiles.Activate(); return; }
                remoteFiles=new RemoteFilesWindow(name,openFiles,strip.Get); holdBar=true;
                remoteFiles.FormClosed+=(_,_)=> { remoteFiles=null; holdBar=false; };
                remoteFiles.Show(this);
            };
            bar.Controls.AddRange([send,get]);
        }
        bar.Controls.AddRange([fullscreen,disconnect,status]); Controls.Add(bar); Controls.Add(screen); bar.BringToFront();
        // Added after the screen so it docks first and the screen fills what is left.
        if(transfers!=null) Controls.Add(transfers);
        Resize+=(_,_)=>PlaceBar(); bar.SizeChanged+=(_,_)=>PlaceBar();
        barTimer.Tick+=(_,_)=>
        {
            bool over=bar.Visible && bar.Bounds.Contains(PointToClient(Cursor.Position));
            if(over || holdBar || menuOpen) { barIdle.Restart(); return; }
            if(bar.Visible && barIdle.Elapsed>barHold) { bar.Visible=false; barTimer.Stop(); screen.Focus(); }
        };
        Shown+=(_,_)=>ShowBar(TimeSpan.FromSeconds(3));
        screen.MouseMove+=(_,e)=>
        {
            if(e.Y<=2) ShowBar(TimeSpan.FromSeconds(1));
            if(Position(e.Location,out int x,out int y)) Post(new("move",X:x,Y:y));
        };
        screen.MouseDown+=(_,e)=> {screen.Focus(); if(!Position(e.Location,out int x,out int y)) return; Post(new("move",X:x,Y:y)); Post(new("button",Value:Button(e.Button),Down:true));};
        screen.MouseUp+=(_,e)=>Post(new("button",Value:Button(e.Button)));
        screen.MouseWheel+=(_,e)=>Post(new("wheel",Value:e.Delta));
        KeyDown+=(_,e)=> {if(e.KeyCode==Keys.F12) {Post(new("release")); ActiveControl=disconnect; return;} if(ActiveControl!=screen) return; e.SuppressKeyPress=true; Post(new("key",Value:e.KeyValue,Down:true));};
        KeyUp+=(_,e)=> {if(e.KeyCode==Keys.F12 || ActiveControl!=screen) return; e.SuppressKeyPress=true; Post(new("key",Value:e.KeyValue));};
        screen.LostFocus+=(_,_)=>Post(new("release")); Deactivate+=(_,_)=>Post(new("release"));
        screen.Presented+=sequence=>
        {
            Post(new("presented",Sequence:sequence)); paintedFrames++;
            if(counter.Elapsed.TotalSeconds>=1 && current!=null)
            {
                double fps=paintedFrames/counter.Elapsed.TotalSeconds;
                double stages=(current.CaptureMicroseconds+current.EncodeMicroseconds)/1000d;
                status.Text=$"{fps:F0} fps • host {stages:F0} ms • round trip {current.RoundTripMilliseconds} ms • {route}";
                paintedFrames=0; counter.Restart();
            }
        };
        FormClosed+=(_,_)=> {barTimer.Dispose(); transfers?.Cancel(); remoteFiles?.Close(); stop.Cancel(); connection.Dispose(); screen.Image?.Dispose(); screen.Image=null;};
        Shown+=async(_,_)=>
        {
            var writing=Task.Run(WriteInput); await Task.Run(ReadFrames); stop.Cancel(); connection.Dispose();
            try {await writing;} catch(OperationCanceledException) { }
        };
    }
    /// <summary>A toolbar button showing the current option ("Balanced ▾") that opens a menu of the options.</summary>
    Button Choice(string[] options,int selected,Action<int> chosen)
    {
        var button=Brand.Style(new Button {Text=options[selected]+"  ▾",AutoSize=true,Margin=new(0,0,8,0)});
        var menu=new ContextMenuStrip {Font=Font,ShowCheckMargin=true,ShowImageMargin=false};
        for(int i=0;i<options.Length;i++)
        {
            int index=i; var item=new ToolStripMenuItem(options[i]) {Checked=i==selected};
            item.Click+=(_,_)=>
            {
                foreach(ToolStripMenuItem other in menu.Items) other.Checked=other==item;
                button.Text=options[index]+"  ▾"; chosen(index);
            };
            menu.Items.Add(item);
        }
        menu.Opened+=(_,_)=>menuOpen=true; menu.Closed+=(_,_)=>menuOpen=false;
        button.Click+=(_,_)=>menu.Show(button,new Point(0,button.Height));
        button.Disposed+=(_,_)=>menu.Dispose();
        return button;
    }
    /// <summary>Reveals the toolbar; it hides again once the pointer has been away from it for <paramref name="hold"/>.</summary>
    void ShowBar(TimeSpan hold)
    {
        barHold=hold; barIdle.Restart();
        if(!bar.Visible) { bar.Visible=true; PlaceBar(); bar.BringToFront(); }
        barTimer.Start();
    }
    void PlaceBar()=>bar.Location=new(Math.Max(0,(ClientSize.Width-bar.Width)/2),0);
    static int Button(MouseButtons button)=>button switch {MouseButtons.Left=>1,MouseButtons.Right=>2,MouseButtons.Middle=>3,_=>0};
    bool Position(Point point,out int x,out int y)
    {
        x=y=0; if(screen.Image==null) return false;
        double ratio=Math.Min((double)screen.Width/screen.Image.Width,(double)screen.Height/screen.Image.Height);
        double width=screen.Image.Width*ratio,height=screen.Image.Height*ratio;
        double px=point.X-(screen.Width-width)/2,py=point.Y-(screen.Height-height)/2;
        if(px<0 || py<0 || px>=width || py>=height) return false;
        x=(int)(px*65535/width); y=(int)(py*65535/height); return true;
    }
    void Post(Packet packet)
    {
        if(stop.IsCancellationRequested || (!control && packet.Kind is "key" or "move" or "button" or "wheel" or "release")) return;
        if(!outbound.Post(packet)) {status.Text="Disconnected • input link stalled"; stop.Cancel(); connection.Dispose();}
    }
    async Task WriteInput()
    {
        try {while(!stop.IsCancellationRequested) {var packet=await outbound.ReadAsync(stop.Token); await connection.Send(packet,stop.Token);}}
        catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException) {stop.Cancel(); connection.Dispose();}
    }
    async Task ReadFrames()
    {
        long lastSequence=0;
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await connection.Read(stop.Token).ConfigureAwait(false);
                var frame=packet.Frame??throw new InvalidDataException("Unsupported stream. Update both PCs to HearthLink 0.2.0.");
                if(frame.Sequence<=lastSequence) throw new InvalidDataException("Out-of-order video frame.");
                lastSequence=frame.Sequence;
                if(frame.Monitor!=Volatile.Read(ref desiredMonitor)) {outbound.Post(new("presented",Sequence:frame.Sequence)); continue;}
                var bitmap=FrameDecoder.Decode(frame); bool transferred=false;
                try
                {
                    await this.InvokeAsync(()=>
                    {
                        if(IsDisposed || stop.IsCancellationRequested) return;
                        var old=screen.Image; screen.Sequence=frame.Sequence; current=frame; screen.Image=bitmap; transferred=true; old?.Dispose(); screen.Invalidate();
                    },stop.Token).ConfigureAwait(false);
                }
                finally {if(!transferred) bitmap.Dispose();}
            }
        }
        catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException or ArgumentException or FormatException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            if(!IsDisposed) {try {await this.InvokeAsync(()=>status.Text="Disconnected • close and reconnect from dashboard");} catch(InvalidOperationException) { }}
        }
        finally {connection.Dispose();}
    }
    void ToggleFull()
    {
        if(!full) {previous=Bounds; FormBorderStyle=FormBorderStyle.None; WindowState=FormWindowState.Normal; Bounds=Screen.FromControl(this).Bounds;}
        else {FormBorderStyle=FormBorderStyle.Sizable; Bounds=previous;}
        full=!full;
    }
}
