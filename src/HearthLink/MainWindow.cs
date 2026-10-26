using HearthLink.Core;
using System.Security.Authentication;

namespace HearthLink;
public sealed class MainWindow : Form
{
    readonly IdentityStore identity;
    readonly PairingGate pairing=new();
    readonly Label hostStatus=new() {AutoSize=true,Padding=new(6),ForeColor=Color.DarkGreen};
    readonly Label codeLabel=new() {AutoSize=true,Padding=new(6)};
    readonly ListView hosts=new() {View=View.Details,FullRowSelect=true,Height=190,Dock=DockStyle.Top,MultiSelect=false};
    readonly ListBox controllers=new() {Height=100,Dock=DockStyle.Top};
    readonly TextBox address=new() {Width=220,PlaceholderText="Remote hostname or IP"};
    readonly CheckBox control=new() {Text="Keyboard and mouse control",AutoSize=true,Checked=true};
    readonly CheckBox hostEnabled=new() {Text="Allow incoming attended sessions",AutoSize=true};
    readonly CheckBox controllerEnabled=new() {Text="Enable controller",AutoSize=true};
    readonly System.Windows.Forms.Timer poll=new() {Interval=10000};
    readonly EmptyDevices emptyDevices=new() {Dock=DockStyle.Fill};
    readonly Label deviceCount=new() {AutoSize=true,ForeColor=Brand.Muted};
    readonly Label emptyControllers=new() {Dock=DockStyle.Fill,Text="No controllers have access to this PC.",ForeColor=Brand.Muted,TextAlign=ContentAlignment.MiddleLeft};
    readonly Label connectionProgress=new() {Text="Add a computer with the pairing code shown on it",ForeColor=Brand.Muted,Font=new("Segoe UI",9),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
    readonly CheckBox lanListener=new() {Text="Also accept direct connections on my network",AutoSize=false};
    readonly System.Windows.Forms.Timer pairingExpiry=new() {Interval=120000};
    HostServer? server;
    RelayPresence? presence;
    string lanStatus="";
    int pairingRound;
    bool polling;
    SessionNotice? notice;
    readonly NotifyIcon sessionTray=new() {Icon=Brand.AppIcon,Text="HearthLink"};
    readonly UpdateCenter updates;
    public bool HasRemoteSession=>server?.HasActiveSession==true || OwnedForms.OfType<Viewer>().Any(v=>!v.IsDisposed);
    public MainWindow(IdentityStore? store=null)
    {
        identity=store??new IdentityStore();
        if(store==null) identity.RelayOverride=Environment.GetEnvironmentVariable("HEARTHLINK_RELAY_URL");
        updates=new(this,identity);
        Text="HearthLink • Your computers"; Size=new(1096,783); FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
        Font=new("Segoe UI",10); BackColor=Brand.Canvas; ForeColor=Brand.Ink; Icon=Brand.AppIcon;
        var root=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        root.ColumnStyles.Add(new(SizeType.Absolute,180)); root.ColumnStyles.Add(new(SizeType.Percent,100)); Controls.Add(root);
        root.RowStyles.Add(new(SizeType.Percent,100));
        var sidebar=new Panel {Dock=DockStyle.Fill,BackColor=Brand.Navy,Margin=Padding.Empty,Padding=new(22,28,22,24)};
        var mark=new BrandMark {Location=new(22,28),Size=new(54,54)}; sidebar.Controls.Add(mark);
        sidebar.Controls.Add(new Label {Text="HearthLink",Location=new(22,100),Size=new(150,30),ForeColor=Color.White,Font=new("Segoe UI",17,FontStyle.Bold)});
        sidebar.Controls.Add(new Label {Text="A little closer to home.",Location=new(22,138),Size=new(144,48),ForeColor=Color.FromArgb(173,193,221),Font=new("Segoe UI",9.5f)});
        sidebar.Controls.Add(new Label {Text="Your computers",Location=new(0,218),Size=new(180,46),Padding=new(22,12,0,0),BackColor=Color.FromArgb(36,57,90),ForeColor=Color.White,Font=new("Segoe UI",10,FontStyle.Bold)});
        sidebar.Controls.Add(new Label {Text="Private connections\nAny network",Location=new(22,292),Size=new(143,70),ForeColor=Color.FromArgb(173,193,221),Font=new("Segoe UI",9.5f)});
        var sidebarFoot=new Label {Text="You approve access.\nYou stay in control.\n\nAttended test build 0.3.0",Dock=DockStyle.Bottom,Height=108,ForeColor=Color.FromArgb(173,193,221),Font=new("Segoe UI",9)}; sidebar.Controls.Add(sidebarFoot);
        root.Controls.Add(sidebar,0,0);
        var content=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=3,ColumnCount=1,Padding=new(28,24,28,18),Margin=Padding.Empty};
        content.RowStyles.Add(new(SizeType.Absolute,86)); content.RowStyles.Add(new(SizeType.Percent,100)); content.RowStyles.Add(new(SizeType.Absolute,36)); root.Controls.Add(content,1,0);
        var heading=new Panel {Dock=DockStyle.Fill};
        heading.Controls.Add(new Label {Text="Your computers",Dock=DockStyle.Top,Height=42,Font=new("Segoe UI Variable Display",25,FontStyle.Bold),ForeColor=Brand.Ink});
        heading.Controls.Add(new Label {Text="A secure connection to the PCs you know.",Location=new(1,48),AutoSize=true,ForeColor=Brand.Muted}); content.Controls.Add(heading,0,0);
        var body=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
        body.ColumnStyles.Add(new(SizeType.Percent,100)); body.ColumnStyles.Add(new(SizeType.Absolute,20)); body.ColumnStyles.Add(new(SizeType.Absolute,318)); content.Controls.Add(body,0,1);
        body.RowStyles.Add(new(SizeType.Percent,100));
        var workspace=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};
        workspace.RowStyles.Add(new(SizeType.Percent,100)); workspace.RowStyles.Add(new(SizeType.Absolute,18)); workspace.RowStyles.Add(new(SizeType.Absolute,205)); body.Controls.Add(workspace,0,0);
        var devices=new Surface {Dock=DockStyle.Fill,Margin=Padding.Empty}; workspace.Controls.Add(devices,0,0);
        var deviceHeading=new Panel {Dock=DockStyle.Fill};
        deviceHeading.Controls.Add(Title("Paired computers",16)); deviceCount.Location=new(0,32); deviceHeading.Controls.Add(deviceCount);
        var refresh=AsyncAction("Refresh",RefreshHosts); refresh.Anchor=AnchorStyles.Top|AnchorStyles.Right; refresh.Location=new(420,0);
        deviceHeading.Controls.Add(refresh); deviceHeading.Resize+=(_,_)=>refresh.Left=deviceHeading.Width-refresh.Width;
        hosts.Dock=DockStyle.Fill; hosts.BorderStyle=BorderStyle.None; hosts.BackColor=Color.White; hosts.ForeColor=Brand.Ink; hosts.HideSelection=false;
        hosts.Columns.Add("Computer",160); hosts.Columns.Add("Address",145); hosts.Columns.Add("Status",120); hosts.Columns.Add("Last seen",155);
        StyleDeviceList();
        var well=new Panel {Dock=DockStyle.Fill}; well.Controls.Add(hosts); well.Controls.Add(emptyDevices);
        address.PlaceholderText="Pairing code (1234-5678-9012) or LAN address"; address.Text=identity.Config.LastTarget; address.Dock=DockStyle.Fill; Brand.StyleInput(address);
        var pairRow=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Padding=new(0,2,0,4),Margin=Padding.Empty};
        pairRow.RowCount=1; pairRow.RowStyles.Add(new(SizeType.Percent,100));
        pairRow.ColumnStyles.Add(new(SizeType.Percent,100)); pairRow.ColumnStyles.Add(new(SizeType.Absolute,151));
        pairRow.Controls.Add(address,0,0); var pairButton=AsyncAction("Pair / connect",PairHost); Brand.Style(pairButton,true); pairButton.AutoSize=false; pairButton.Dock=DockStyle.Fill; pairButton.Margin=new(10,0,0,0); pairRow.Controls.Add(pairButton,1,0);
        address.KeyDown+=(_,e)=> {if(e.KeyCode==Keys.Enter) {e.SuppressKeyPress=true; pairButton.PerformClick();}};
        control.Text="Keyboard & mouse"; control.UseMnemonic=false; control.ForeColor=Brand.Ink; control.Margin=new(0,10,12,0);
        var connect=AsyncAction("Connect",ConnectHost); Brand.Style(connect,true);
        var forget=Action("Remove PC",()=> { if(hosts.SelectedIndices.Count>0) RemovePc(identity.Config.Hosts[hosts.SelectedIndices[0]].Fingerprint); });
        devices.Controls.Add(Stack((deviceHeading,64),(well,-1),(connectionProgress,26),(pairRow,42),(Row(control,connect,forget),44)));
        var access=new Surface {Dock=DockStyle.Fill,Margin=Padding.Empty}; workspace.Controls.Add(access,0,2);
        controllers.Dock=DockStyle.Fill; controllers.BorderStyle=BorderStyle.None; controllers.BackColor=Color.White; controllers.ForeColor=Brand.Ink; controllers.HorizontalScrollbar=true;
        controllers.DrawMode=DrawMode.OwnerDrawFixed; controllers.ItemHeight=42;
        controllers.DrawItem+=(_,e)=> { if(e.Index<0) return; var item=(ControllerItem)controllers.Items[e.Index]; bool selected=(e.State&DrawItemState.Selected)!=0; using var brush=new SolidBrush(selected?Color.FromArgb(233,241,255):Color.White); e.Graphics.FillRectangle(brush,e.Bounds); TextRenderer.DrawText(e.Graphics,item.Name,Font,new Point(e.Bounds.Left+6,e.Bounds.Top+3),Brand.Ink); using var small=new Font("Consolas",8); TextRenderer.DrawText(e.Graphics,item.Fingerprint,small,new Point(e.Bounds.Left+6,e.Bounds.Top+23),Brand.Muted); e.DrawFocusRectangle(); };
        var controllerWell=new Panel {Dock=DockStyle.Fill}; controllerWell.Controls.Add(controllers); controllerWell.Controls.Add(emptyControllers);
        var revoke=Action("Remove PC",()=> { if(controllers.SelectedItem is ControllerItem item) RemovePc(item.Fingerprint); }); Brand.Style(revoke,danger:true);
        access.Controls.Add(Stack((Title("Who can request access",14),30),(Caption("Approved controllers still need your permission to connect."),28),(controllerWell,-1),(Row(revoke,Action("Session history",OpenHistory)),40)));
        var local=new Surface {Dock=DockStyle.Fill,Margin=Padding.Empty,Padding=new(18)}; body.Controls.Add(local,2,0);
        var localScroll=new Panel {Dock=DockStyle.Fill,AutoScroll=true}; local.Controls.Add(localScroll);
        var localStack=new TableLayoutPanel {Dock=DockStyle.Top,ColumnCount=1,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Margin=Padding.Empty}; localScroll.Controls.Add(localStack);
        localStack.ColumnStyles.Add(new(SizeType.Percent,100));
        void Local(Control item,int height) { int row=localStack.RowCount++; localStack.RowStyles.Add(new(SizeType.Absolute,height)); if(item is Button button) button.AutoSize=false; item.Dock=DockStyle.Fill; item.Margin=Padding.Empty; localStack.Controls.Add(item,0,row); }
        Local(Title("This computer",16),42);
        var name=new TextBox {Text=identity.Config.Name,MaxLength=64,Dock=DockStyle.Fill}; Brand.StyleInput(name);
        var nameRow=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty}; nameRow.RowStyles.Add(new(SizeType.Percent,100)); nameRow.ColumnStyles.Add(new(SizeType.Percent,100)); nameRow.ColumnStyles.Add(new(SizeType.Absolute,72)); nameRow.Controls.Add(name,0,0);
        var save=Action("Save",()=> { identity.Config.Name=name.Text.Trim().Length>0?name.Text.Trim():Environment.MachineName; identity.Save(); }); save.AutoSize=false; save.Padding=new(4); save.Dock=DockStyle.Fill; save.Margin=new(8,0,0,6); nameRow.Controls.Add(save,1,0); Local(nameRow,42);
        hostEnabled.Text="Allow incoming sessions"; controllerEnabled.Text="Use as a controller";
        hostEnabled.Checked=identity.Config.Host; controllerEnabled.Checked=identity.Config.Controller;
        lanListener.Checked=identity.Config.LanListener;
        hostEnabled.CheckedChanged+=(_,_)=> { identity.Config.Host=hostEnabled.Checked; identity.Save(); ConfigureHost(); };
        lanListener.CheckedChanged+=(_,_)=> { identity.Config.LanListener=lanListener.Checked; identity.Save(); ConfigureHost(); };
        controllerEnabled.CheckedChanged+=(_,_)=> { identity.Config.Controller=controllerEnabled.Checked; identity.Save(); };
        Local(hostEnabled,34); Local(lanListener,48); Local(controllerEnabled,34);
        hostStatus.AutoSize=false; hostStatus.Font=new("Segoe UI",9); hostStatus.Padding=new(10); hostStatus.BackColor=Color.FromArgb(239,245,253); hostStatus.ForeColor=Brand.Muted; Local(hostStatus,62);
        Local(Action("End remote session",()=>server?.Disconnect()),40); Local(new Panel(),16);
        Local(Action("App updates",updates.Show),40); Local(new Panel(),12);
        Local(Title("Pair with this PC",13),32);
        Local(Action("Open pairing for 2 minutes",()=>OpenPairing()),40);
        codeLabel.AutoSize=false; codeLabel.Text="Pairing is closed"; codeLabel.Font=new("Segoe UI",11,FontStyle.Bold); codeLabel.ForeColor=Brand.Muted; codeLabel.Padding=new(0,10,0,0); Local(codeLabel,46);
        Local(Caption("This PC’s identity · SHA-256"),28);
        var fingerprint=new TextBox {Text=identity.Fingerprint,ReadOnly=true,Multiline=true,WordWrap=true,Font=new("Consolas",10),BorderStyle=BorderStyle.None,BackColor=Color.FromArgb(242,246,251),ForeColor=Brand.Ink,TabStop=true}; Local(fingerprint,64);
        Local(new Label {Text="Compare every digit on both PCs before approving a pairing request.",ForeColor=Brand.Muted,Font=new("Segoe UI",9)},50);
        Local(new Label {Text="Attended access only\nKeep this PC signed in and unlocked.\nClose HearthLink to stop hosting.",ForeColor=Brand.Muted,Font=new("Segoe UI",9),Padding=new(0,10,0,0)},90);
        content.Controls.Add(new Label {Text="Encrypted end to end  •  You approve access  •  Works on any network",Dock=DockStyle.Fill,TextAlign=ContentAlignment.BottomLeft,ForeColor=Brand.Muted,Font=new("Segoe UI",9)},0,2);
        PopulateHosts(); PopulateControllers();
        Shown+=async(_,_)=> { ConfigureHost(); await RefreshHosts(); poll.Start(); if(store==null) updates.Start(); };
        poll.Tick+=async(_,_)=>await RefreshHosts();
        pairingExpiry.Tick+=(_,_)=>ClosePairing("Pairing is closed");
        FormClosing+=(_,_)=> { poll.Stop(); pairingExpiry.Stop(); updates.Dispose(); presence?.Dispose(); server?.Dispose(); notice?.CloseAfterSession(); notice=null; sessionTray.Visible=false; sessionTray.Dispose(); };
    }
    sealed record ControllerItem(string Name,string Fingerprint) { public override string ToString()=>Name+" • "+Fingerprint; }
    static Label Title(string text,int size)=>new() {Text=text,Font=new("Segoe UI",size,FontStyle.Bold),ForeColor=Brand.Ink,Dock=DockStyle.Top,Height=size+15};
    static Label Caption(string text)=>new() {Text=text,ForeColor=Brand.Muted,Font=new("Segoe UI",9),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
    static TableLayoutPanel Stack(params (Control Item,int Height)[] items)
    {
        var stack=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=items.Length,Margin=Padding.Empty};
        stack.ColumnStyles.Add(new(SizeType.Percent,100));
        for(int i=0;i<items.Length;i++) { stack.RowStyles.Add(items[i].Height<0?new(SizeType.Percent,100):new(SizeType.Absolute,items[i].Height)); items[i].Item.Dock=DockStyle.Fill; items[i].Item.Margin=Padding.Empty; stack.Controls.Add(items[i].Item,0,i); } return stack;
    }
    static FlowLayoutPanel Row(params Control[] controls) { var row=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,Padding=new(0,3,0,0),Margin=Padding.Empty}; row.Controls.AddRange(controls); return row; }
    static Button Action(string label,System.Action action) { var b=Brand.Style(new Button {Text=label,AutoSize=true}); b.Click+=(_,_)=>action(); return b; }
    void OpenHistory()
    {
        string file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HearthLink","history.jsonl");
        if(!File.Exists(file)) { MessageBox.Show(this,"No session history yet. Connection attempts will appear here.","HearthLink"); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe",file) {UseShellExecute=true});
    }
    void StyleDeviceList()
    {
        hosts.OwnerDraw=true;
        hosts.DrawColumnHeader+=(_,e)=> { using var brush=new SolidBrush(Color.FromArgb(246,248,252)); e.Graphics.FillRectangle(brush,e.Bounds); TextRenderer.DrawText(e.Graphics,e.Header?.Text??"",new Font("Segoe UI",9),new Rectangle(e.Bounds.X+8,e.Bounds.Y,e.Bounds.Width-8,e.Bounds.Height),Brand.Muted,TextFormatFlags.VerticalCenter); };
        hosts.DrawItem+=(_,e)=> { if(e.Item.Selected) { using var b=new SolidBrush(Color.FromArgb(235,242,255)); e.Graphics.FillRectangle(b,e.Bounds); } };
        hosts.DrawSubItem+=(_,e)=> { if(e.Item==null || e.SubItem==null) return; Color color=e.ColumnIndex==2 && e.SubItem.Text.StartsWith("Online")?Color.FromArgb(14,116,88):e.ColumnIndex==0?Brand.Ink:Brand.Muted; TextRenderer.DrawText(e.Graphics,e.SubItem.Text,Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y,e.Bounds.Width-8,e.Bounds.Height),color,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis); };
        hosts.Resize+=(_,_)=> { if(hosts.Width<100) return; int total=Math.Max(400,hosts.ClientSize.Width-6); int[] weights=[27,25,22,26]; for(int i=0;i<4;i++) hosts.Columns[i].Width=total*weights[i]/100; };
    }
    Button AsyncAction(string label,Func<Task> action)
    {
        var b=Brand.Style(new Button {Text=label,AutoSize=true});
        b.Click+=async(_,_)=> { b.Enabled=false; try { await action(); } catch(Exception e) { connectionProgress.Text="Connection did not complete • try again when the remote PC is ready"; MessageBox.Show(this,Explain(e),"HearthLink",MessageBoxButtons.OK,MessageBoxIcon.Warning); } finally { if(!b.IsDisposed) b.Enabled=true; } }; return b;
    }
    static string Explain(Exception e)=>e is AuthenticationException ? "Host identity could not be verified. If the host was reinstalled, compare its new fingerprint in person before removing and pairing again." : e.Message;
    public Task<T> OnUi<T>(Func<T> action)
    {
        if(IsDisposed || Disposing) return Task.FromCanceled<T>(new CancellationToken(true));
        if(!InvokeRequired) return Task.FromResult(action());
        var result=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { BeginInvoke(()=> { try { result.SetResult(action()); } catch(Exception e) { result.SetException(e); } }); }
        catch(InvalidOperationException) { result.TrySetCanceled(); }
        return result.Task;
    }
    public void SessionStatus(string text)=>hostStatus.Text=text;
    public void SessionStarted(string controller,bool allowControl,Action endSession)
    {
        controller=SessionNoticeText.Controller(controller);
        hostStatus.Text="CONNECTED • "+controller+(allowControl?" • CONTROL":" • VIEW ONLY");
        hostStatus.BackColor=Color.FromArgb(226,246,237); hostStatus.ForeColor=Color.FromArgb(12,100,72);
        notice?.CloseAfterSession(); notice=new(controller,allowControl,endSession); notice.Show(this);
        sessionTray.Text="HearthLink • remote session active"; sessionTray.Visible=true;
        sessionTray.ShowBalloonTip(5000,"HearthLink • computer connected",SessionNoticeText.Message(controller,allowControl),ToolTipIcon.Info);
    }
    public void SessionEnded()
    {
        notice?.CloseAfterSession(); notice=null; sessionTray.Visible=false;
        hostStatus.BackColor=Color.FromArgb(239,245,253); hostStatus.ForeColor=Brand.Muted;
        ShowHostReady();
    }
    void ShowHostReady()
    {
        if(server?.HasActiveSession==true) return;
        hostStatus.Text=presence==null?"Host disabled":(presence.State is {Length:>0} state?state:RelayPresence.Connecting)+" • attended approval required"+lanStatus;
    }
    void ConfigureHost()
    {
        ClosePairing("Pairing is closed");
        presence?.Dispose(); presence=null; server?.Dispose(); server=null; lanStatus="";
        if(!identity.Config.Host) { hostStatus.Text="Host disabled"; return; }
        var host=server=new(identity,this);
        if(identity.Config.LanListener)
        {
            try { host.Start(); lanStatus=" • LAN TCP 45831"; }
            catch(System.Net.Sockets.SocketException e) { lanStatus=" • LAN listener unavailable: "+e.Message; }
        }
        presence=new(identity,()=>identity.Config.Controllers.Fingerprints(),(stream,from)=>host.ServeRelay(stream,from));
        presence.StateChanged+=_=>OnUi(()=> { ShowHostReady(); return true; });
        presence.Start(); ShowHostReady();
    }
    /// <summary>Opens the pairing gate and returns its 8-digit secret. The 12-digit relay code replaces it once the relay confirms a lookup.</summary>
    public string OpenPairing()
    {
        string secret=pairing.Open(DateTimeOffset.UtcNow); int round=++pairingRound;
        pairingExpiry.Stop(); pairingExpiry.Start(); codeLabel.ForeColor=Brand.Blue;
        if(presence is {State:RelayPresence.Online} relay) { codeLabel.Text="Getting a pairing code…"; _=AnnouncePairing(relay,secret,round); }
        else codeLabel.Text=LanPairingText(secret);
        return secret;
    }
    /// <summary>Without the relay, only the 8-digit code over a direct connection can pair, and only if this PC listens on the network.</summary>
    string LanPairingText(string secret)=>server?.Listening==true ? secret+"  •  LAN only  •  expires in 2 minutes" : "Relay unreachable • turn on direct connections to pair on your network";
    async Task AnnouncePairing(RelayPresence relay,string secret,int round)
    {
        // Another host may hold the same 4-digit lookup; the relay refuses it and a fresh one is tried.
        for(int attempt=0;attempt<5 && relay.State==RelayPresence.Online;attempt++)
        {
            var code=PairingCode.New(secret);
            if(!await relay.OpenPairing(code.Lookup)) continue;
            if(round==pairingRound) codeLabel.Text=code.Display+"  •  expires in 2 minutes";
            return;
        }
        if(round==pairingRound) codeLabel.Text=LanPairingText(secret);
    }
    void ClosePairing(string text)
    {
        pairingExpiry.Stop(); pairingRound++;
        codeLabel.Text=text; codeLabel.ForeColor=Brand.Muted;
        if(presence is {} relay) _=relay.ClosePairing();
    }
    /// <summary>A LAN peer address as the host saw it, or null for "relay:…" peers.</summary>
    static string? LanAddress(string peer)=>System.Net.IPAddress.TryParse(peer,out _)?peer:null;
    void TrustChanged() { PopulateHosts(); PopulateControllers(); if(presence is {} relay) _=relay.PublishAllowed(); }
    void RemovePc(string fingerprint) { identity.Remove(fingerprint); server?.Disconnect(); TrustChanged(); }
    /// <summary>Called by the host after a trusted controller's pinned exchange.</summary>
    public bool UpgradeTrust(string fingerprint,string peer)
    {
        if(!identity.UpgradeTrust(fingerprint,LanAddress(peer))) return false;
        TrustChanged(); return true;
    }
    public bool PairController(string fingerprint,string name,string code,string peer)
    {
        if(!PeerName.Valid(name) || !pairing.Consume(code,DateTimeOffset.UtcNow,peer)) { identity.Audit(fingerprint,"pair-rejected"); return false; }
        ClosePairing("Code used • open a new pairing window");
        if(MessageBox.Show(this,$"Trust this PC both ways?\n\nPC name: {name}\nCompare this fingerprint with the other PC's own identity:\n{fingerprint}\n\nEach PC will be able to connect to the other. The other PC will still need approval for each session.","HearthLink pairing",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) return false;
        identity.TrustBothWays(fingerprint,name,LanAddress(peer)); identity.Audit(fingerprint,"paired"); TrustChanged(); return true;
    }
    void PopulateControllers() { controllers.Items.Clear(); foreach(var entry in identity.Config.Controllers.Entries) controllers.Items.Add(new ControllerItem(PeerName.Clean(entry.Value),entry.Key)); emptyControllers.Visible=controllers.Items.Count==0; controllers.Visible=controllers.Items.Count>0; }
    void PopulateHosts()
    {
        hosts.Items.Clear(); foreach(var host in identity.Config.Hosts) hosts.Items.Add(new ListViewItem([PeerName.Clean(host.Name),host.Address.Length>0?host.Address:"Relay only","Not checked",host.LastSeen?.ToLocalTime().ToString("g")??"Never"]));
        emptyDevices.Visible=hosts.Items.Count==0; hosts.Visible=hosts.Items.Count>0;
        deviceCount.Text=hosts.Items.Count==1?"1 approved computer":$"{hosts.Items.Count} approved computers";
    }
    async Task PairHost()
    {
        if(!identity.Config.Controller) throw new InvalidOperationException("Enable controller first.");
        string target=address.Text.Trim(); if(target.Length==0) throw new InvalidOperationException("Enter the pairing code shown on the other PC, or its LAN address.");
        // A pairing code is a one-time secret: it goes through the relay and is never saved as the last target.
        if(PairingCode.TryParse(target,out var relayCode)) { await PairByCode(relayCode); return; }
        identity.Config.LastTarget=target; identity.Save();
        // Match only the address the user typed; host names come from the remote PC and must never select a saved identity.
        int existing=identity.Config.Hosts.FindIndex(p=>p.Address.Equals(target,StringComparison.OrdinalIgnoreCase));
        if(existing>=0)
        {
            foreach(ListViewItem item in hosts.Items) item.Selected=false;
            hosts.Items[existing].Selected=true; hosts.Items[existing].EnsureVisible();
            connectionProgress.Text="Using saved identity • requesting connection";
            await ConnectHost(); return;
        }
        connectionProgress.Text="Connecting to "+target+"…";
        string? fingerprint=null;
        using var connection=await Connection.Connect(target,identity,null,actual=> { fingerprint=actual; return ConfirmHost(actual); });
        string? code=Prompt("Enter the one-time code shown on the remote PC"); if(code==null) {connectionProgress.Text="Pairing cancelled • computer name saved"; return;}
        if(PairingCode.TryParse(code,out var full)) code=full.Secret;
        connectionProgress.Text="Waiting for pairing approval on "+target;
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        if(await connection.Pair(identity,code,target,target,timeout.Token)==null) throw new InvalidOperationException("Pairing rejected. Open a new pairing window on the host, check the code, and approve the request there.");
        await Paired(fingerprint!);
    }
    async Task PairByCode(PairingCode code)
    {
        connectionProgress.Text="Finding the other PC through the relay…";
        string? fingerprint=null;
        using var connection=await RelayClient.ConnectForPairing(identity.RelayUrl,identity,code,actual=> { fingerprint=actual; return ConfirmHost(actual); },CancellationToken.None);
        connectionProgress.Text="Waiting for pairing approval on the other PC";
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        if(await connection.Pair(identity,code.Secret,"Paired PC",null,timeout.Token)==null) throw new InvalidOperationException("Pairing rejected. Open a new pairing window on the other PC, check the code, and approve the request there.");
        await Paired(fingerprint!);
    }
    bool ConfirmHost(string actual)=>OnUi(()=>MessageBox.Show(this,$"Compare ALL digits against the fingerprint shown on the remote PC:\n\n{actual}\n\nPairing is two-way: each PC will be able to request access to the other. Every session still needs approval on the PC being accessed.\n\nDo they match exactly?","Verify host identity",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes).GetAwaiter().GetResult();
    async Task Paired(string fingerprint)
    {
        TrustChanged();
        int paired=identity.Config.Hosts.FindIndex(p=>p.Fingerprint==fingerprint);
        foreach(ListViewItem item in hosts.Items) item.Selected=false;
        hosts.Items[paired].Selected=true; hosts.Items[paired].EnsureVisible();
        connectionProgress.Text="Paired • requesting session approval"; await ConnectHost();
    }
    async Task ConnectHost()
    {
        if(!identity.Config.Controller) throw new InvalidOperationException("Enable controller first.");
        if(hosts.SelectedIndices.Count==0) throw new InvalidOperationException("Select a trusted host first.");
        var host=identity.Config.Hosts[hosts.SelectedIndices[0]];
        string name=PeerName.Clean(host.Name);
        connectionProgress.Text="Connecting to "+name+"…";
        Connection? connection=null;
        try
        {
            (connection,bool direct)=await Reach(host,name);
            string route=direct?"direct":"relay";
            connectionProgress.Text="Waiting for session approval on "+name+" • "+route;
            await connection.Send(new("session",Control:control.Checked,Value:Wire.ProtocolVersion));
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var reply=await connection.Read(timeout.Token);
            if(reply.Kind!="ready") throw new InvalidOperationException(reply.Text??"Host rejected the session.");
            if(reply.Value!=Wire.ProtocolVersion) throw new InvalidOperationException("Update HearthLink on BOTH PCs to version 0.3.0 or later. Existing pairing is preserved.");
            connectionProgress.Text="Connected to "+name;
            identity.Audit(host.Fingerprint,"outbound-connected");
            if(identity.UpgradeTrust(host.Fingerprint,null)) TrustChanged();
            new Viewer(connection,name,control.Checked,reply.Monitors??["Display 1"],route).Show(this); connection=null;
        }
        finally { connection?.Dispose(); }
    }
    /// <summary>
    /// Tries the relay and the saved LAN address at once and keeps the first pinned connection, so PCs on the same network
    /// get the direct path's lower latency. The other attempt is closed when it finishes.
    /// </summary>
    async Task<(Connection Connection,bool Direct)> Reach(Peer host,string name)
    {
        var relay=RelayClient.ConnectHost(identity.RelayUrl,identity,host.Fingerprint,CancellationToken.None);
        Task<Connection>? lan=host.Address.Length>0?Connection.Connect(host.Address,identity,host.Fingerprint):null;
        var running=new List<Task<Connection>> {relay}; if(lan!=null) running.Add(lan);
        while(running.Count>0)
        {
            var done=await Task.WhenAny(running); running.Remove(done);
            if(!done.IsCompletedSuccessfully) continue;
            foreach(var other in running) _=other.ContinueWith(t=> { if(t.IsCompletedSuccessfully) t.Result.Dispose(); else _=t.Exception; },TaskScheduler.Default);
            return (done.Result,done==lan);
        }
        var relayFailure=relay.Exception!.InnerException!;
        // Anything but "unreachable" or "offline" (not paired, no answer, wrong identity) is the real answer.
        if(relayFailure is not (RelayException {CloseCode:0 or RelayProtocol.Offline} or ArgumentException)) throw relayFailure;
        if(lan==null) throw new InvalidOperationException(relayFailure is RelayException {CloseCode:RelayProtocol.Offline} ? $"Not reachable through the relay. Check HearthLink is running on {name}." : relayFailure.Message,relayFailure);
        var lanFailure=lan.Exception!.InnerException!;
        if(lanFailure is AuthenticationException) throw lanFailure;
        throw new InvalidOperationException($"Not reachable through the relay or on your network. Check HearthLink is running on {name}.",lanFailure);
    }
    async Task RefreshHosts()
    {
        if(polling) return; polling=true;
        int trusted=identity.Config.Controllers.Entries.Count;
        try
        {
            var all=identity.Config.Hosts.ToArray();
            HashSet<string> relayed=[];
            try { using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(10)); relayed=await RelayClient.Online(identity.RelayUrl,identity,all.Select(p=>p.Fingerprint),limit.Token); }
            catch(Exception e) when(e is RelayException or ArgumentException or OperationCanceledException or IOException) { /* LAN probes below still run */ }
            foreach(var host in all)
            {
                string state="Offline"; DateTimeOffset? seen=host.LastSeen;
                if(relayed.Contains(host.Fingerprint)) { state="Online"; seen=DateTimeOffset.UtcNow; }
                else if(host.Address.Length>0)
                {
                    try
                    {
                        using var c=await Connection.Connect(host.Address,identity,host.Fingerprint);
                        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var reply=await c.Probe(identity,timeout.Token);
                        if(reply.Kind=="online") { state="Online (network)"; seen=DateTimeOffset.UtcNow; }
                        else state="Trust revoked";
                    }
                    catch(AuthenticationException) { state="Identity mismatch"; }
                    catch(Exception e) when(e is IOException or System.Net.Sockets.SocketException or OperationCanceledException) { }
                }
                int index=identity.Config.Hosts.FindIndex(p=>p.Fingerprint==host.Fingerprint && p.Address==host.Address);
                if(index<0) continue;
                identity.Config.Hosts[index]=host with {LastSeen=seen};
                if(index<hosts.Items.Count) { hosts.Items[index].SubItems[2].Text=state; hosts.Items[index].SubItems[3].Text=seen?.ToLocalTime().ToString("g")??"Never"; }
            }
            identity.Save();
            // Repopulating hosts here would wipe the statuses just shown.
            if(identity.Config.Controllers.Entries.Count!=trusted) { PopulateControllers(); _=presence?.PublishAllowed(); }
        }
        finally { polling=false; }
    }
    string? Prompt(string label)
    {
        using var dialog=new Form {Text="HearthLink pairing",ClientSize=new(440,212),BackColor=Brand.Canvas,Font=Font,Icon=Brand.AppIcon,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false};
        var box=new TextBox {Left=24,Top=82,Width=392,MaxLength=14,TextAlign=HorizontalAlignment.Center,Font=new("Consolas",20),BorderStyle=BorderStyle.FixedSingle};
        var ok=Brand.Style(new Button {Text="Pair computer",DialogResult=DialogResult.OK,Left=264,Top=150,Width=152},true);
        dialog.Controls.AddRange([new Label {Text="Enter the pairing code",Left=24,Top=20,Width=390,Height=28,Font=new("Segoe UI",15,FontStyle.Bold),ForeColor=Brand.Ink},new Label {Text=label,Left=24,Top=53,Width=392,ForeColor=Brand.Muted,Font=new("Segoe UI",9)},box,ok]); dialog.AcceptButton=ok;
        return dialog.ShowDialog(this)==DialogResult.OK?box.Text.Trim():null;
    }
}
