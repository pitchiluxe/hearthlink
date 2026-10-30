using HearthLink.Core;
using System.Diagnostics;

namespace HearthLink;
public sealed class Viewer : Form
{
    readonly Connection connection;
    sealed class RemoteScreen : PictureBox
    {
        public event Action<long>? Presented;
        public RemoteScreen() {SetStyle(ControlStyles.Selectable|ControlStyles.OptimizedDoubleBuffer,true); TabStop=true;}
        protected override bool IsInputKey(Keys keyData)=>true;
        /// <summary>
        /// Shows a decoded frame and reports it presented at once. Waiting for a paint would stall the session whenever the
        /// viewer is minimized or covered (Windows sends no paint then), and the host stops after <see cref="FrameWindow.StallLimit"/> without acknowledgements.
        /// </summary>
        public void ShowFrame(Image image,long sequence) { var old=Image; Image=image; old?.Dispose(); Invalidate(); Presented?.Invoke(sequence); }
    }
    readonly RemoteScreen screen=new() {Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.Black};
    readonly Label status=new() {ForeColor=Color.White,AutoEllipsis=true,Width=310,Height=38,Padding=new(8)};
    readonly CancellationTokenSource stop=new();
    readonly InputMailbox outbound=new();
    readonly bool control;
    readonly Stopwatch counter=Stopwatch.StartNew();
    int desiredMonitor,shownFrames;
    VideoFrame? current;
    bool full;
    Rectangle previous;
    // The toolbar floats over the remote screen and stays hidden, so only the connected PC is visible.
    readonly FlowLayoutPanel bar=new() {AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new(12,10,12,8),BackColor=Brand.Navy,WrapContents=false,Visible=false};
    readonly System.Windows.Forms.Timer barTimer=new() {Interval=150};
    readonly Stopwatch barIdle=new();
    TimeSpan barHold;
    // Open while a file dialog or browser is up, so the toolbar does not slide away under it.
    bool holdBar, menuOpen, closing,sessionLost;
    TransferStrip? transfers;
    FileManagerWindow? fileManager;
    // Covers the last frame once the session ends, so a dead session never looks like a frozen but live one.
    readonly Panel lost=new() {Visible=false,BackColor=Brand.Navy,Size=new(480,150),Padding=new(16)};
    readonly Label lostText=new() {Dock=DockStyle.Fill,ForeColor=Color.White,TextAlign=ContentAlignment.MiddleCenter};
    readonly Func<CancellationToken,Task<Connection>>? filesFactory;
    readonly Action<TransferStrip>? observeTransfer;
    readonly AiRuntime? ai;
    readonly PerformanceAgent? performance;
    int currentQuality;
    bool manualQuality,agentBusy;
    string agentStatus="Local AI agent not configured";
    DateTimeOffset lastFrameAt;
    [System.ComponentModel.Browsable(false)]
    public string AgentStatus=>agentStatus;
    public event Action? SessionStateChanged;
    [System.ComponentModel.Browsable(false)]
    public bool SessionActive=>!stop.IsCancellationRequested && !closing && !sessionLost;
    [System.ComponentModel.Browsable(false)]
    public bool CanTransfer=>SessionActive && control && filesFactory!=null;
    public Viewer(Connection c,string name,bool allowControl,string[] monitors,string route="relay",Func<CancellationToken,Task<Connection>>? openFiles=null,Func<Task>? reconnect=null,Action<TransferStrip>? observeTransfers=null,int initialQuality=0,AiRuntime? aiRuntime=null)
    {
        connection=c; control=allowControl;
        filesFactory=openFiles; observeTransfer=observeTransfers;
        ai=aiRuntime;
        if(ai!=null)
        {
            performance=new(()=>ai.Options,(sample,config,token)=>ai.Background(sample,config,token,message=>
            {if(!manualQuality && SessionActive && ReferenceEquals(config,ai.Options)) SetAgentStatus(message);}));
            agentStatus=ai.Options.PerformanceAgent?"Local AI monitoring (no cloud)":"Local AI agent off";
        }
        Text="HearthLink • "+name; Width=1280; Height=800; MinimumSize=new(640,400); KeyPreview=true; Icon=Brand.AppIcon; Font=new("Segoe UI",10);
        // Menu buttons, as in TeamViewer's toolbar: a menu pops up above everything, where combo box lists failed to open on some PCs.
        var displays=Choice(monitors,0,index=> {Volatile.Write(ref desiredMonitor,index); Post(new("monitor",Value:index));});
        initialQuality=Math.Clamp(initialQuality,0,DesktopCapture.Profiles.Length-1);
        currentQuality=initialQuality;
        var profile=Choice(DesktopCapture.Profiles,initialQuality,index=>
        {
            currentQuality=index; manualQuality=true; performance?.ManualOverride();
            SetAgentStatus("Local AI paused: manual quality"); Post(new("profile",Value:index));
        });
        if(initialQuality!=0) Post(new("profile",Value:initialQuality));
        var fullscreen=Brand.Style(new Button {Text="Full screen",AutoSize=true}); fullscreen.Click+=(_,_)=>ToggleFull();
        var disconnect=Brand.Style(new Button {Text="Disconnect",AutoSize=true},true); disconnect.Click+=(_,_)=>Close();
        status.Text=(control?"Connected • Control":"Connected • View only")+" • "+route;
        bar.Controls.AddRange([displays,profile]);
        if(control && openFiles!=null)
        {
            var strip=transfers=new TransferStrip(openFiles);
            observeTransfers?.Invoke(strip);
            var send=Brand.Style(new Button {Text="Send files",AutoSize=true});
            send.Click+=async(_,_)=>
            {
                using var pick=new OpenFileDialog {Multiselect=true,Title="Send files to "+name};
                holdBar=true; bool chosen=pick.ShowDialog(this)==DialogResult.OK; holdBar=false;
                if(chosen) await strip.Send(pick.FileNames);
            };
            var get=Brand.Style(new Button {Text="File transfer",AutoSize=true});
            get.Click+=(_,_)=>
            {
                OpenFileManager();
            };
            bar.Controls.AddRange([send,get]);
            // Files dragged from Explorer onto the remote screen are sent like Send files.
            screen.AllowDrop=true;
            screen.DragEnter+=(_,e)=>e.Effect=e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;
            screen.DragDrop+=async(_,e)=>
            {
                if(e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
                var items=TransferStrip.Expand(paths);
                if(items.Count==0) { strip.Notice("Nothing to send: those names cannot be copied."); return; }
                if(items.Count>=TransferStrip.MaxItems) { strip.Notice($"Too many items: drop fewer than {TransferStrip.MaxItems:N0} files and folders at a time."); return; }
                await strip.Send(items);
            };
        }
        bar.Controls.AddRange([fullscreen,disconnect,status]); Controls.Add(bar); Controls.Add(screen); bar.BringToFront();
        // Added after the screen so it docks first and the screen fills what is left.
        if(transfers!=null) Controls.Add(transfers);
        var lostButtons=new FlowLayoutPanel {Dock=DockStyle.Bottom,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
        var close=Brand.Style(new Button {Text="Close",AutoSize=true}); close.Click+=(_,_)=>Close();
        lostButtons.Controls.Add(close);
        if(reconnect!=null)
        {
            var again=Brand.Style(new Button {Text="Reconnect",AutoSize=true},true);
            again.Click+=(_,_)=> { Close(); _=reconnect(); };
            lostButtons.Controls.Add(again);
        }
        lost.Controls.AddRange([lostText,lostButtons]); Controls.Add(lost);
        Resize+=(_,_)=> { PlaceBar(); PlaceLost(); }; bar.SizeChanged+=(_,_)=>PlaceBar();
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
            lastFrameAt=DateTimeOffset.UtcNow;
            Post(new("presented",Sequence:sequence)); shownFrames++;
            if(counter.Elapsed.TotalSeconds>=1 && current!=null)
            {
                double fps=shownFrames/counter.Elapsed.TotalSeconds;
                double stages=(current.CaptureMicroseconds+(long)current.EncodeMicroseconds)/1000d;
                status.Text=$"{fps:F0} fps • host {stages:F0} ms • round trip {current.RoundTripMilliseconds} ms • {route} • {agentStatus}";
                if(performance!=null && !agentBusy && !manualQuality && WindowState!=FormWindowState.Minimized && Visible)
                    _=ConsiderQuality(new(fps,current.RoundTripMilliseconds,stages,currentQuality,shownFrames),profile);
                shownFrames=0; counter.Restart();
            }
        };
        FormClosed+=(_,_)=> {closing=true; barTimer.Dispose(); transfers?.Cancel(); fileManager?.Close(); stop.Cancel(); Drop(); screen.Image?.Dispose(); screen.Image=null; SessionStateChanged?.Invoke();};
        Shown+=async(_,_)=>
        {
            var writing=Task.Run(WriteInput); await Task.Run(ReadFrames); stop.Cancel(); Drop();
            try {await writing;} catch(OperationCanceledException) { }
        };
    }
    public void OpenFileManager()
    {
        if(!CanTransfer) return;
        if(fileManager is {IsDisposed:false}) {fileManager.Activate(); return;}
        fileManager=new FileManagerWindow(Text["HearthLink • ".Length..],filesFactory!,observeTransfer,ai);
        fileManager.FormClosed+=(_,_)=>fileManager=null;
        fileManager.Show(this);
    }
    void SetAgentStatus(string text)
    {
        if(agentStatus==text) return; agentStatus=text; SessionStateChanged?.Invoke();
    }
    async Task ConsiderQuality(PerformanceSample sample,Button profile)
    {
        if(performance==null || ai==null || agentBusy || stop.IsCancellationRequested) return;
        agentBusy=true;
        try
        {
            if(!ai.Options.PerformanceAgent) {SetAgentStatus("Local AI agent off"); return;}
            if(currentQuality<=ai.Options.MinimumQuality) {SetAgentStatus("Local AI: already at minimum quality"); return;}
            if(currentQuality>ai.Options.MaximumQuality) {SetAgentStatus("Local AI: current quality is outside its allowed range"); return;}
            int? suggested=await performance.Evaluate(sample,DateTimeOffset.UtcNow,()=>currentQuality,
                ()=>SessionActive && !manualQuality && WindowState!=FormWindowState.Minimized && Visible &&
                    DateTimeOffset.UtcNow-lastFrameAt<TimeSpan.FromSeconds(3),stop.Token);
            if(suggested is {} next && next!=currentQuality)
            {
                if(current==null || (current.RoundTripMilliseconds<180 && (current.CaptureMicroseconds+(long)current.EncodeMicroseconds)/1000d<100))
                {SetAgentStatus("Local AI: connection recovered; quality unchanged"); return;}
                currentQuality=next; Post(new("profile",Value:next)); profile.Text=DesktopCapture.Profiles[next]+"  ▾";
                if(profile.Tag is ContextMenuStrip menu)
                    for(int i=0;i<menu.Items.Count;i++) if(menu.Items[i] is ToolStripMenuItem item) item.Checked=i==next;
                SetAgentStatus("Local AI selected "+DesktopCapture.Profiles[next]+" for congestion");
            }
        }
        catch(Exception e) when(e is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
        {if(!stop.IsCancellationRequested) SetAgentStatus("Local AI unavailable; quality unchanged");}
        finally {agentBusy=false;}
    }
    /// <summary>
    /// Closes the session connection on a pool thread. Closing a relay tunnel waits up to 3 s for the close handshake,
    /// which would freeze the window if done on the UI thread.
    /// </summary>
    void Drop()=>_=Task.Run(connection.Dispose);
    void PlaceLost()=>lost.Location=new(Math.Max(0,(ClientSize.Width-lost.Width)/2),Math.Max(0,(ClientSize.Height-lost.Height)/2));
    /// <summary>Shows why the session ended over the remote screen, with Reconnect and Close.</summary>
    void Lose(string message)
    {
        if(IsDisposed || lost.Visible) return;
        status.Text="Disconnected"; lostText.Text=message;
        sessionLost=true;
        PlaceLost(); lost.Visible=true; lost.BringToFront();
        transfers?.Cancel();
        SessionStateChanged?.Invoke();
    }
    /// <summary>A toolbar button showing the current option ("Balanced ▾") that opens a menu of the options.</summary>
    Button Choice(string[] options,int selected,Action<int> chosen)
    {
        var button=Brand.Style(new Button {Text=options[selected]+"  ▾",AutoSize=true,Margin=new(0,0,8,0)});
        var menu=new ContextMenuStrip {Font=Font,ShowCheckMargin=true,ShowImageMargin=false};
        button.Tag=menu;
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
        if(!outbound.Post(packet)) {stop.Cancel(); Drop(); Lose("The connection to the other PC stopped responding.");}
    }
    async Task WriteInput()
    {
        try {while(!stop.IsCancellationRequested) {var packet=await outbound.ReadAsync(stop.Token); await connection.Send(packet,stop.Token);}}
        catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException) {stop.Cancel(); Drop();}
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
                        current=frame; transferred=true; screen.ShowFrame(bitmap,frame.Sequence);
                    },stop.Token).ConfigureAwait(false);
                }
                finally {if(!transferred) bitmap.Dispose();}
            }
        }
        catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException or ArgumentException or FormatException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            // Closing the window ends the read too; anything else means the session ended under the viewer.
            if(!IsDisposed && !closing) {try {await this.InvokeAsync(()=>Lose("The session with the other PC ended. It may have lost its connection or been ended there."));} catch(InvalidOperationException) { }}
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
