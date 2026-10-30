using HearthLink.Core;
using System.Text.Json;

namespace HearthLink;

public sealed partial class MainWindow
{
    readonly Panel pageDeck=new() {Dock=DockStyle.Fill,Margin=Padding.Empty};
    readonly Dictionary<string,Control> pages=[];
    readonly Dictionary<string,Button> navigation=[];
    readonly Dictionary<string,string> peerStates=new(StringComparer.Ordinal);
    readonly TextBox computerSearch=new() {Name="ComputerSearch",PlaceholderText="Search computers or addresses",Dock=DockStyle.Fill};
    readonly CheckBox favoritesOnly=new() {Text="Favorites",Dock=DockStyle.Fill,AutoSize=true};
    readonly Label noMatches=new() {Text="No matching computers. Clear search or turn off Favorites.",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Visible=false};
    readonly DataGridView historyGrid=ActivityGrid("Time","Computer","Event");
    readonly DataGridView recentGrid=ActivityGrid("Last connected","Computer","Address");
    readonly DataGridView healthGrid=ActivityGrid("Computer","Status","Last seen");
    readonly DataGridView transferGrid=ActivityGrid("Started","Computer","State","Progress","Details");
    readonly TextBox historySearch=new() {PlaceholderText="Search computer, fingerprint or event",Dock=DockStyle.Fill};
    readonly Label historyNote=Note("");
    readonly Label recentNote=Note("");
    readonly Label healthNote=Note("");
    readonly ComboBox liveSessions=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=300,DisplayMember="Text"};
    readonly Label transferNote=Note("Transfers are shown for this app run. Start a control session to copy files.");
    readonly Button activeSession=new() {Text="Active session",Visible=false};
    readonly CancellationTokenSource navigationLifetime=new();
    readonly Dictionary<Guid,TrackedTransfer> transferActivity=[];
    readonly Dictionary<TransferStrip,Action<TransferActivity>> transferObservers=[];
    string currentPage="Your computers",relayCheck="Not checked yet";
    DateTimeOffset? lastSuccessfulCheck;
    sealed record TrackedTransfer(string Peer,TransferActivity Activity,Action? Cancel);

    static Label Note(string text)=>new() {Text=text,UseMnemonic=false,Dock=DockStyle.Fill,ForeColor=Brand.Muted,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
    static DataGridView ActivityGrid(params string[] columns)
    {
        var grid=new DataGridView {Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
            AllowUserToResizeRows=false,RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,
            GridColor=Brand.Line,EnableHeadersVisualStyles=false,ClipboardCopyMode=DataGridViewClipboardCopyMode.EnableWithoutHeaderText};
        grid.DefaultCellStyle=new DataGridViewCellStyle {Font=new Font("Segoe UI",9.5f),ForeColor=Brand.Ink,BackColor=Color.White,SelectionBackColor=Color.FromArgb(225,237,255),SelectionForeColor=Brand.Ink,Padding=new Padding(5)};
        grid.ColumnHeadersDefaultCellStyle=new DataGridViewCellStyle {Font=new Font("Segoe UI",9.5f,FontStyle.Bold),ForeColor=Brand.Muted,BackColor=Color.FromArgb(246,248,252),Padding=new Padding(5)};
        grid.RowTemplate.Height=36; grid.ColumnHeadersHeight=38;
        foreach(string column in columns) grid.Columns.Add(column,column);
        return grid;
    }
    Control Page(string title,string description,params (Control Item,int Height)[] rows)
    {
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new(28,24,28,18),Margin=Padding.Empty};
        layout.ColumnStyles.Add(new(SizeType.Percent,100));
        layout.RowStyles.Add(new(SizeType.Absolute,50)); layout.RowStyles.Add(new(SizeType.Absolute,48)); layout.RowStyles.Add(new(SizeType.Percent,100));
        layout.Controls.Add(new Label {Text=title,UseMnemonic=false,Dock=DockStyle.Fill,Font=new("Segoe UI Variable Display",25,FontStyle.Bold),ForeColor=Brand.Ink},0,0);
        layout.Controls.Add(Note(description),0,1);
        var surface=new Surface {Dock=DockStyle.Fill,Margin=Padding.Empty}; surface.Controls.Add(Stack(rows)); layout.Controls.Add(surface,0,2);
        return layout;
    }
    Control ScrollSettings(params (Control Item,int Height)[] rows)
    {
        var scroll=new Panel {Dock=DockStyle.Fill,AutoScroll=true};
        var stack=new TableLayoutPanel {Dock=DockStyle.Top,Height=rows.Sum(r=>r.Height),ColumnCount=1,RowCount=rows.Length,Margin=Padding.Empty};
        stack.ColumnStyles.Add(new(SizeType.Percent,100));
        for(int i=0;i<rows.Length;i++)
        {
            stack.RowStyles.Add(new(SizeType.Absolute,rows[i].Height)); rows[i].Item.Dock=DockStyle.Fill;
            rows[i].Item.Margin=Padding.Empty; stack.Controls.Add(rows[i].Item,0,i);
        }
        scroll.Controls.Add(stack); return scroll;
    }
    void BuildNavigation(TableLayoutPanel root,Panel sidebar,Control home,Control securityList,Control nameEditor)
    {
        root.Controls.Remove(home); root.Controls.Add(pageDeck,1,0);
        pages.Add("Your computers",home);
        var accessController=new CheckBox {Name="ControllerAccess",Text="Use as a controller",AutoSize=true,Checked=controllerEnabled.Checked};
        accessController.CheckedChanged+=(_,_)=>controllerEnabled.Checked=accessController.Checked;
        controllerEnabled.CheckedChanged+=(_,_)=>accessController.Checked=controllerEnabled.Checked;
        // Reparent these controls once. Navigation only changes page visibility.
        pages.Add("Access & security",Page("Access & security","Choose who can access this PC. Removing a PC revokes both roles.",
            (hostEnabled,38),(accessController,38),(lanListener,48),(unattended,48),
            (Note("Unattended changes require Windows Hello on this PC. Pairing and session approval rules remain in effect."),50),
            (Row(Action("End remote session",()=>server?.Disconnect())),46),(securityList,-1)));
        var defaultControl=new CheckBox {Name="DefaultControl",Text="Request keyboard and mouse control by default",Checked=identity.Config.DefaultControl,AutoSize=true};
        var quality=new ComboBox {Name="DefaultQuality",DropDownStyle=ComboBoxStyle.DropDownList,Width=230};
        quality.Items.AddRange(DesktopCapture.Profiles); quality.SelectedIndex=Math.Clamp(identity.Config.DefaultQuality,0,DesktopCapture.Profiles.Length-1);
        var autoUpdates=new CheckBox {Name="AutoUpdates",Text="Automatically check for updates at launch and daily",Checked=identity.Config.CheckUpdates,AutoSize=true};
        var settingsNote=Note("Changes apply to new sessions. Existing sessions keep their current quality and access mode.");
        var recentControl=new CheckBox {Text="Keyboard & mouse",UseMnemonic=false,Checked=control.Checked,AutoSize=true,Margin=new(0,10,10,0)};
        recentControl.CheckedChanged+=(_,_)=>control.Checked=recentControl.Checked;
        control.CheckedChanged+=(_,_)=>recentControl.Checked=control.Checked;
        var saveDefaults=Action("Save preferences",()=>
        {
            try
            {
                bool previousControl=identity.Config.DefaultControl,previousUpdates=identity.Config.CheckUpdates; int previousQuality=identity.Config.DefaultQuality;
                identity.Config.DefaultControl=defaultControl.Checked; identity.Config.DefaultQuality=quality.SelectedIndex; identity.Config.CheckUpdates=autoUpdates.Checked;
                try {identity.Save();}
                catch {identity.Config.DefaultControl=previousControl; identity.Config.DefaultQuality=previousQuality; identity.Config.CheckUpdates=previousUpdates; throw;}
                control.Checked=defaultControl.Checked; settingsNote.Text="Preferences saved. They apply to new sessions.";
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) {settingsNote.Text="Could not save. Check disk space and access to your profile folder.";}
        });
        var general=ScrollSettings(
            (Title("Computer name",14),34),(nameEditor,46),(controllerEnabled,40),(Title("New sessions",14),48),
            (defaultControl,40),(Note("Default image quality"),30),(Row(quality),44),(autoUpdates,48),
            (Row(saveDefaults),48),(settingsNote,64));
        var settingsTabs=new TabControl {Name="SettingsTabs",Dock=DockStyle.Fill};
        var generalTab=new TabPage("General") {BackColor=Color.White}; generalTab.Controls.Add(general); settingsTabs.TabPages.Add(generalTab);
        var aiTab=new TabPage("AI providers & performance") {BackColor=Color.White}; aiTab.Controls.Add(new AiSettingsPanel(identity,ai)); settingsTabs.TabPages.Add(aiTab);
        pages.Add("Settings",Page("Settings","Your computer, AI providers and session preferences.",(settingsTabs,-1)));
        pages.Add("Recent sessions",Page("Recent sessions","Reconnect to a currently paired computer. The host's approval settings still apply.",
            (recentGrid,-1),(recentNote,44),(Row(recentControl,AsyncAction("Connect selected",ConnectRecent),Action("Refresh",LoadRecent)),46)));
        historySearch.TextChanged+=(_,_)=>LoadHistory();
        pages.Add("Session history",Page("Session history","Recent local audit metadata. Search by computer, fingerprint or event.",
            (historySearch,40),(historyGrid,-1),(historyNote,42),(Row(Action("Refresh",LoadHistory),Action("Export history…",ExportHistory)),46)));
        pages.Add("Connection health",Page("Connection health","Current host state and the last availability checks. Unknown status is not offline.",
            (healthNote,100),(healthGrid,-1),(Row(AsyncAction("Check connections",RefreshHosts),Action("Refresh details",RefreshHealth)),46)));
        pages.Add("File transfers",Page("File transfers","Manage transfers started from your live control sessions.",
            (Row(liveSessions,Action("Open file manager",()=>SelectedViewer()?.OpenFileManager())),48),
            (transferGrid,-1),(transferNote,44),(Row(Action("Cancel selected",CancelTransfer),Action("Refresh sessions",RefreshSessionShortcut)),46)));
        pages.Add("Help & updates",Page("Help & updates","Guidance for pairing, control sessions and updates.",
            (ScrollSettings(
                (Title("Get connected",16),44),
                (Note("1. Open HearthLink on both PCs. On the host, open Access & security and allow incoming sessions."),58),
                (Note("2. In Your computers, open pairing on the host. Enter its code on the controller and compare every fingerprint digit before approving."),74),
                (Note("3. Select the paired PC and Connect. The host approves access unless unattended access was enabled there with Windows Hello."),66),
                (Note("During a session, move to the top edge for controls. F12 releases keyboard focus. Use End remote session on the host to stop access."),66),
                (Row(Action("App updates",updates.Show),Action("Release downloads",()=>OpenLink("https://github.com/pitchiluxe/hearthlink/releases"))),48),
                (Row(Action("User guide",()=>OpenLink("https://hearthlink.vercel.app/assets/HearthLink-Illustrated-Manual-0.1.1.pdf"))),48),
                (Note("The illustrated guide covers the original interface. The instructions above describe the current navigation."),52),
                (Note("Locked desktops, secure Windows prompts and reboot recovery are not supported. Keep the host signed in and HearthLink running."),62),
                (Note("HearthLink "+UpdateCenter.Installed+" • Created by Erick Omari"),40)),-1)));
        foreach(var (name,page) in pages) {page.Visible=false; page.Name="Page:"+name; pageDeck.Controls.Add(page);}

        foreach(Control old in sidebar.Controls.Cast<Control>().ToArray()) old.Dispose();
        sidebar.Padding=Padding.Empty;
        var rail=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};
        rail.ColumnStyles.Add(new(SizeType.Percent,100));
        rail.RowStyles.Add(new(SizeType.Absolute,185)); rail.RowStyles.Add(new(SizeType.Percent,100)); rail.RowStyles.Add(new(SizeType.Absolute,116));
        var brand=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
        brand.Controls.Add(new BrandMark {Location=new(22,24),Size=new(46,46)});
        brand.Controls.Add(new Label {Text="HearthLink",Location=new(22,82),Size=new(180,34),ForeColor=Color.White,Font=new("Segoe UI",18,FontStyle.Bold)});
        brand.Controls.Add(new Label {Text="Your PCs, a little closer.",Location=new(22,127),Size=new(172,30),ForeColor=Color.FromArgb(173,193,221),Font=new("Segoe UI",9)});
        rail.Controls.Add(brand,0,0);
        var menu=new Panel {Dock=DockStyle.Fill,AutoScroll=true,Margin=Padding.Empty};
        var links=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,Margin=Padding.Empty}; links.ColumnStyles.Add(new(SizeType.Percent,100));
        int row=0;
        foreach(string name in new[]{"Your computers","Recent sessions","File transfers","Access & security","Connection health","Session history","Settings"})
        {
            var button=NavigationButton(name); links.RowStyles.Add(new(SizeType.Absolute,48)); links.Controls.Add(button,0,row++);
        }
        StyleNav(activeSession); activeSession.Click+=(_,_)=>
        {
            var viewer=OwnedForms.OfType<Viewer>().FirstOrDefault(v=>v.SessionActive);
            if(viewer!=null) {if(viewer.WindowState==FormWindowState.Minimized) viewer.WindowState=FormWindowState.Normal; viewer.Activate();}
            else Navigate("Access & security");
        };
        links.RowStyles.Add(new(SizeType.Absolute,48)); links.Controls.Add(activeSession,0,row);
        menu.Controls.Add(links); rail.Controls.Add(menu,0,1);
        var bottom=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
        bottom.ColumnStyles.Add(new(SizeType.Percent,100));
        bottom.RowStyles.Add(new(SizeType.Absolute,48)); bottom.RowStyles.Add(new(SizeType.Percent,100));
        bottom.Controls.Add(NavigationButton("Help & updates"),0,0);
        bottom.Controls.Add(new Label {Text="Created by Erick Omari\nVersion "+UpdateCenter.Installed,Dock=DockStyle.Fill,Padding=new(20,10,0,0),ForeColor=Color.FromArgb(173,193,221),Font=new("Segoe UI",8.5f)},0,1);
        rail.Controls.Add(bottom,0,2); sidebar.Controls.Add(rail);
        updates.AvailabilityChanged+=version=>navigation["Help & updates"].Text=version==null?"Help & updates":"Help & updates  •";
        FormClosing+=(_,_)=>
        {
            navigationLifetime.Cancel();
            foreach(var pair in transferObservers) pair.Key.ActivityChanged-=pair.Value;
            transferObservers.Clear();
        };
        Navigate("Your computers");
    }
    static void StyleNav(Button button)
    {
        button.Dock=DockStyle.Fill; button.Margin=Padding.Empty; button.FlatStyle=FlatStyle.Flat;
        button.FlatAppearance.BorderSize=0; button.FlatAppearance.MouseOverBackColor=Color.FromArgb(36,57,90);
        button.BackColor=Brand.Navy; button.ForeColor=Color.FromArgb(206,219,237); button.Font=new("Segoe UI",10);
        button.Padding=new(18,0,4,0); button.TextAlign=ContentAlignment.MiddleLeft; button.UseVisualStyleBackColor=false;
        button.UseMnemonic=false; button.Cursor=Cursors.Hand;
    }
    Button NavigationButton(string name)
    {
        var button=new Button {Text=name,Name="Nav:"+name,AccessibleName=name}; StyleNav(button);
        button.Click+=(_,_)=>Navigate(name); navigation.Add(name,button); return button;
    }
    void Navigate(string name)
    {
        if(!pages.ContainsKey(name)) return;
        pageDeck.SuspendLayout();
        currentPage=name;
        foreach(var pair in pages) pair.Value.Visible=pair.Key==name;
        pages[name].BringToFront();
        foreach(var pair in navigation)
        {
            bool selected=pair.Key==name; pair.Value.BackColor=selected?Color.FromArgb(36,57,90):Brand.Navy;
            pair.Value.ForeColor=selected?Color.White:Color.FromArgb(206,219,237);
            pair.Value.AccessibleDescription=selected?"Current section":"Open "+pair.Key;
        }
        Text="HearthLink • "+name;
        if(name=="Session history") LoadHistory();
        if(name=="Recent sessions") LoadRecent();
        if(name=="Connection health") RefreshHealth();
        if(name=="File transfers") RefreshSessionShortcut();
        if(name=="Settings")
        {
            // App updates can also change this preference from its own window.
            var auto=FindControl<CheckBox>(pages[name],"AutoUpdates"); if(auto!=null) auto.Checked=identity.Config.CheckUpdates;
        }
        pageDeck.ResumeLayout();
    }
    static T? FindControl<T>(Control root,string name) where T:Control=>root.Controls.Find(name,true).OfType<T>().FirstOrDefault();
    bool IsFavorite(string fingerprint)=>identity.Config.Favorites?.Contains(fingerprint)==true;
    IEnumerable<Peer> VisiblePeers()=>identity.Config.Hosts
        .Where(p=>(!favoritesOnly.Checked || IsFavorite(p.Fingerprint)) &&
            (p.Name.Contains(computerSearch.Text,StringComparison.OrdinalIgnoreCase) || p.Address.Contains(computerSearch.Text,StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(p=>IsFavorite(p.Fingerprint)).ThenBy(p=>p.Name,StringComparer.OrdinalIgnoreCase);
    Peer? SelectedPeer()=>hosts.SelectedItems.Count==0?null:identity.Config.Hosts.Find(p=>Equals(hosts.SelectedItems[0].Tag,p.Fingerprint));
    void SelectPeer(string fingerprint)
    {
        computerSearch.Clear(); favoritesOnly.Checked=false; PopulateHosts();
        foreach(ListViewItem item in hosts.Items) {item.Selected=Equals(item.Tag,fingerprint); if(item.Selected) item.EnsureVisible();}
    }
    void ToggleFavorite()
    {
        if(SelectedPeer() is not {} peer) {connectionProgress.Text="Select a computer to change its favorite status."; return;}
        identity.Config.Favorites??=[];
        bool wasFavorite=IsFavorite(peer.Fingerprint);
        if(wasFavorite) identity.Config.Favorites.Remove(peer.Fingerprint); else identity.Config.Favorites.Add(peer.Fingerprint);
        try {identity.Save(); PopulateHosts();}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException)
        {
            if(wasFavorite) identity.Config.Favorites.Add(peer.Fingerprint); else identity.Config.Favorites.Remove(peer.Fingerprint);
            connectionProgress.Text="Favorite could not be saved. Check disk space and profile folder access.";
        }
    }
    void UpdateDeviceCount()
    {
        var states=identity.Config.Hosts.Select(p=>peerStates.GetValueOrDefault(p.Fingerprint,"Not checked")).ToArray();
        int online=states.Count(s=>s.StartsWith("Online",StringComparison.Ordinal));
        int unknown=states.Count(s=>s is "Not checked" or "Status unavailable");
        deviceCount.Text=$"{hosts.Items.Count} shown / {states.Length} paired • {online} online • {states.Length-online-unknown} offline • {unknown} unknown";
    }
    string PeerLabel(string fingerprint)=>identity.Config.Hosts.Find(p=>p.Fingerprint==fingerprint)?.Name is {} name?PeerName.Clean(name):
        identity.Config.Controllers.Entries.GetValueOrDefault(fingerprint) is {} controller?PeerName.Clean(controller):fingerprint[..12]+"… (unpaired)";
    IReadOnlyList<HistoryEntry> ReadHistory(Label note)
    {
        try {var entries=identity.ReadHistory(); note.Text=$"{entries.Count} recent records (up to 1,000, from the last 512 KiB)."; return entries;}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) {note.Text="History cannot be read. Check profile folder access and try Refresh."; return [];}
    }
    void LoadHistory()
    {
        var entries=ReadHistory(historyNote); string query=historySearch.Text;
        historyGrid.Rows.Clear();
        foreach(var item in entries)
        {
            string name=PeerLabel(item.Peer);
            if(!string.Join(" ",name,item.Peer,item.Outcome).Contains(query,StringComparison.OrdinalIgnoreCase)) continue;
            int index=historyGrid.Rows.Add(item.At.ToLocalTime().ToString("g"),name,item.Outcome.Replace('-',' '));
            historyGrid.Rows[index].Cells[1].ToolTipText=item.Peer;
        }
        if(entries.Count==0 && !historyNote.Text.StartsWith("History cannot")) historyNote.Text="No session history yet. Connection and access events will appear here.";
    }
    void LoadRecent()
    {
        var entries=ReadHistory(recentNote).Where(e=>e.Outcome=="outbound-connected").DistinctBy(e=>e.Peer);
        recentGrid.Rows.Clear();
        foreach(var entry in entries)
        {
            var peer=identity.Config.Hosts.Find(p=>p.Fingerprint==entry.Peer); if(peer==null) continue;
            int row=recentGrid.Rows.Add(entry.At.ToLocalTime().ToString("g"),PeerName.Clean(peer.Name),peer.Address.Length>0?peer.Address:"Relay only");
            recentGrid.Rows[row].Tag=peer.Fingerprint;
        }
        if(!recentNote.Text.StartsWith("History cannot")) recentNote.Text=recentGrid.Rows.Count==0?"No recent connections to currently paired computers.":"Only computers still paired with this PC are shown.";
    }
    async Task ConnectRecent()
    {
        if(recentGrid.SelectedRows.Count==0 || recentGrid.SelectedRows[0].Tag is not string fingerprint) throw new InvalidOperationException("Select a recent computer first.");
        await ConnectTo(fingerprint,control.Checked);
    }
    void ExportHistory()
    {
        var entries=ReadHistory(historyNote);
        if(entries.Count==0) return;
        using var save=new SaveFileDialog {Filter="History metadata (*.jsonl)|*.jsonl",FileName="HearthLink-history.jsonl",OverwritePrompt=true};
        if(save.ShowDialog(this)!=DialogResult.OK) return;
        try
        {
            File.WriteAllLines(save.FileName,entries.Select(e=>JsonSerializer.Serialize(new {at=e.At,peer=e.Peer,outcome=e.Outcome})));
            historyNote.Text="History exported. Includes timestamps, peer fingerprints and event types.";
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) {historyNote.Text="Could not export to that location. Choose a writable folder.";}
    }
    void RefreshHealth()
    {
        healthNote.Text=$"This PC: {hostStatus.Text}\n{relayCheck}\nLast successful relay check: {(lastSuccessfulCheck is {} checkedAt?checkedAt.ToString("g"):"Not yet")}\n"+
            string.Join(" | ",OwnedForms.OfType<Viewer>().Where(v=>v.SessionActive).Select(v=>v.AgentStatus).DefaultIfEmpty("No live viewer; AI performance agent waits for a session."));
        healthGrid.Rows.Clear();
        foreach(var peer in identity.Config.Hosts) healthGrid.Rows.Add(PeerName.Clean(peer.Name),peerStates.GetValueOrDefault(peer.Fingerprint,"Not checked"),Seen(peer.LastSeen));
    }
    Viewer? SelectedViewer()=>liveSessions.SelectedItem is Viewer viewer && viewer.CanTransfer?viewer:null;
    void RefreshSessionShortcut()
    {
        if(IsDisposed || navigationLifetime.IsCancellationRequested) return;
        var viewers=OwnedForms.OfType<Viewer>().Where(v=>v.SessionActive).ToArray();
        activeSession.Visible=viewers.Length>0 || incoming!=null;
        activeSession.Text=viewers.Length+ (incoming==null?0:1)>1?"Active sessions  • "+(viewers.Length+(incoming==null?0:1)):"Active session";
        var selected=liveSessions.SelectedItem; liveSessions.Items.Clear();
        liveSessions.Items.AddRange(viewers.Where(v=>v.CanTransfer).Cast<object>().ToArray());
        if(selected!=null && liveSessions.Items.Contains(selected)) liveSessions.SelectedItem=selected;
        else if(liveSessions.Items.Count>0) liveSessions.SelectedIndex=0;
        if(transferActivity.Count==0) transferNote.Text=liveSessions.Items.Count==0?"Start a control session, then open its file manager here. View-only sessions cannot transfer files.":"Choose a session and Open file manager. Transfers appear here as they start.";
        if(currentPage=="Connection health") RefreshHealth();
    }
    void ObserveTransfer(TransferStrip strip,string peer)
    {
        if(transferObservers.ContainsKey(strip)) return;
        Action<TransferActivity> handler=activity=>
        {
            if(navigationLifetime.IsCancellationRequested) return;
            bool active=activity.State is "Preparing" or "Copying";
            transferActivity[activity.Id]=new(peer,activity,active?strip.Cancel:null);
            foreach(var entry in transferActivity.Values.Where(t=>t.Cancel==null).OrderByDescending(t=>t.Activity.Started).Skip(200).ToArray()) transferActivity.Remove(entry.Activity.Id);
            RefreshTransfers();
        };
        transferObservers.Add(strip,handler); strip.ActivityChanged+=handler;
        strip.Disposed+=(_,_)=>
        {
            strip.ActivityChanged-=handler; transferObservers.Remove(strip);
            foreach(var entry in transferActivity.Values.Where(t=>t.Cancel?.Target==strip).ToArray())
                transferActivity[entry.Activity.Id]=entry with {Cancel=null,Activity=entry.Activity with {State="Cancelled",Message="Session closed"}};
            if(!navigationLifetime.IsCancellationRequested) RefreshTransfers();
        };
    }
    void RefreshTransfers()
    {
        object? selected=transferGrid.SelectedRows.Count==0?null:transferGrid.SelectedRows[0].Tag;
        transferGrid.Rows.Clear();
        foreach(var entry in transferActivity.Values.OrderByDescending(t=>t.Activity.Started))
        {
            var a=entry.Activity; int row=transferGrid.Rows.Add(a.Started.ToString("t"),entry.Peer,a.State,a.Percent+"%",a.Message);
            transferGrid.Rows[row].Tag=a.Id; if(Equals(selected,a.Id)) transferGrid.Rows[row].Selected=true;
        }
        int active=transferActivity.Values.Count(t=>t.Cancel!=null);
        navigation["File transfers"].Text=active>0?$"File transfers  • {active}":"File transfers";
        transferNote.Text=$"{active} active • {transferActivity.Count-active} finished this run. Select an active transfer to cancel.";
    }
    void CancelTransfer()
    {
        if(transferGrid.SelectedRows.Count>0 && transferGrid.SelectedRows[0].Tag is Guid id && transferActivity.TryGetValue(id,out var activity)) activity.Cancel?.Invoke();
    }
    void OpenLink(string url)
    {
        try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) {UseShellExecute=true});}
        catch(Exception e) when(e is System.ComponentModel.Win32Exception or InvalidOperationException) {MessageBox.Show(this,"Windows could not open the link. Check your default browser.","HearthLink");}
    }
}
