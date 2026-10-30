using HearthLink.Core;
using System.Runtime.InteropServices;
using System.Security.Authentication;

namespace HearthLink;
/// <summary>One side of the file manager: this PC (left) or the other PC (right).</summary>
public interface IFileSource
{
    Task<(string Folder,IReadOnlyList<FileEntry> Entries)> List(string folder);
    Task<IReadOnlyList<DriveEntry>> Drives();
    /// <summary>Creates a new folder in <paramref name="parent"/> under a name that exists nowhere yet; returns its full path.</summary>
    Task<string> MakeFolder(string parent,string name);
    string Home { get; }
}
public sealed class LocalFiles : IFileSource
{
    public string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public Task<(string Folder,IReadOnlyList<FileEntry> Entries)> List(string folder)=>Task.Run(()=> { var (full,entries)=FileHost.Read(folder); return (full,(IReadOnlyList<FileEntry>)entries); });
    public Task<IReadOnlyList<DriveEntry>> Drives()=>Task.Run(()=>(IReadOnlyList<DriveEntry>)FileHost.Drives().ToArray());
    public Task<string> MakeFolder(string parent,string name)=>Task.Run(()=> { string path=FileNames.Unique(parent,name); Directory.CreateDirectory(path); return path; });
}
/// <summary>The other PC through its own files connection, one request at a time.</summary>
public sealed class RemoteFiles(Func<CancellationToken,Task<Connection>> open,CancellationToken lifetime) : IFileSource, IDisposable
{
    readonly SemaphoreSlim one=new(1,1);
    FileClient? client;
    IReadOnlyList<DriveEntry>? drives;
    public string Home { get; private set; }="";
    /// <summary>
    /// Runs one request. The browsing connection sits idle while the user reads a list, and the relay closes silent
    /// connections, so a request that fails on a reused connection is retried once on a fresh one.
    /// </summary>
    async Task<T> Use<T>(Func<FileClient,Task<T>> action)
    {
        await one.WaitAsync(lifetime);
        try
        {
            for(int attempt=0;;attempt++)
            {
                bool reused=client!=null;
                client??=new FileClient(await open(lifetime));
                try { return await action(client); }
                catch(Exception e) when(e is IOException or InvalidDataException or ObjectDisposedException)
                {
                    client.Dispose(); client=null;
                    if(!reused || attempt>0) throw;
                }
            }
        }
        finally { one.Release(); }
    }
    public async Task<(string Folder,IReadOnlyList<FileEntry> Entries)> List(string folder)
    {
        var listing=await Use(c=>c.List(folder,lifetime));
        if(folder.Length==0) { Home=listing.Folder; drives=listing.Drives; }
        return (listing.Folder,listing.Entries);
    }
    public async Task<IReadOnlyList<DriveEntry>> Drives() { if(drives==null) await List(""); return drives??[]; }
    /// <summary>Refreshes free space by asking for the start listing again.</summary>
    public void ForgetDrives()=>drives=null;
    public async Task<string> MakeFolder(string parent,string name)=>Path.Combine(parent,await Use(c=>c.Folder(name,lifetime,parent)));
    public void Dispose() { client?.Dispose(); one.Dispose(); }
}
/// <summary>Small shell icons by file type, as Explorer shows them, without touching the file (it may be on the other PC).</summary>
static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct FileInfo { public IntPtr Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Display; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] public string Type; }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path,uint attributes,ref FileInfo info,uint size,uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    public static readonly ImageList Images=new() {ColorDepth=ColorDepth.Depth32Bit,ImageSize=new(16,16)};
    /// <summary>The image key for a folder, a drive root or a file name's type.</summary>
    public static string Key(string name,bool folder)
    {
        string key=folder?"\\folder":"."+Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if(Images.Images.ContainsKey(key)) return key;
        var info=new FileInfo();
        // SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES; FILE_ATTRIBUTE_DIRECTORY or NORMAL.
        if(SHGetFileInfo(folder?"folder":"file"+key,folder?0x10u:0x80u,ref info,(uint)Marshal.SizeOf<FileInfo>(),0x111)!=IntPtr.Zero && info.Icon!=IntPtr.Zero)
        {
            using(var icon=Icon.FromHandle(info.Icon)) Images.Images.Add(key,(Icon)icon.Clone());
            DestroyIcon(info.Icon);
        }
        return key;
    }
}
/// <summary>A Total Commander style pane: drive buttons, drive space, path, the folder's list and a selection summary.</summary>
public sealed class FilePane : Panel
{
    readonly IFileSource source;
    readonly Label header=new() {Dock=DockStyle.Top,Height=30,Font=new("Segoe UI",10,FontStyle.Bold),TextAlign=ContentAlignment.MiddleLeft,Padding=new(8,0,0,0)};
    readonly FlowLayoutPanel drives=new() {Dock=DockStyle.Top,Height=34,Padding=new(4,3,4,0),WrapContents=false,AutoScroll=false,BackColor=Color.FromArgb(240,243,248)};
    readonly Label space=new() {Dock=DockStyle.Top,Height=24,ForeColor=Brand.Muted,TextAlign=ContentAlignment.MiddleLeft,Padding=new(8,0,0,0),Font=new("Segoe UI",9)};
    readonly Label path=new() {Dock=DockStyle.Top,Height=24,TextAlign=ContentAlignment.MiddleLeft,Padding=new(8,0,0,0),Font=new("Segoe UI",9,FontStyle.Bold),AutoEllipsis=true};
    readonly Label summary=new() {Dock=DockStyle.Bottom,Height=24,ForeColor=Brand.Muted,TextAlign=ContentAlignment.MiddleLeft,Padding=new(8,0,0,0),Font=new("Segoe UI",9),AutoEllipsis=true};
    public readonly ListView List=new() {View=View.Details,FullRowSelect=true,Dock=DockStyle.Fill,MultiSelect=true,HideSelection=false,BorderStyle=BorderStyle.None,SmallImageList=ShellIcons.Images};
    readonly Button driveMenu=new() {Width=74,Height=26,Margin=new(0,0,8,0),FlatStyle=FlatStyle.Flat,Font=new("Segoe UI",9,FontStyle.Bold),TextAlign=ContentAlignment.MiddleLeft};
    readonly ContextMenuStrip drivesList=new() {ShowCheckMargin=true,ShowImageMargin=false,Font=new("Segoe UI",9.5f)};
    Font? bold;
    IReadOnlyList<DriveEntry> driveList=[];
    IReadOnlyList<FileEntry> shown=[];
    public string Folder { get; private set; }="";
    /// <summary>What the open folder holds, as last listed.</summary>
    public IReadOnlyList<FileEntry> Entries=>shown;
    public string Title { get; }
    public event Action<FilePane>? Activated;
    public FilePane(string title,IFileSource files)
    {
        source=files; Title=title; Dock=DockStyle.Fill; BackColor=Color.White; BorderStyle=BorderStyle.FixedSingle;
        header.Text=title;
        List.Columns.Add("Name",260); List.Columns.Add("Ext",60); List.Columns.Add("Size",100,HorizontalAlignment.Right); List.Columns.Add("Date",140);
        Controls.Add(List); Controls.Add(summary); Controls.Add(path); Controls.Add(space); Controls.Add(drives); Controls.Add(header);
        List.Resize+=(_,_)=> { int w=Math.Max(300,List.ClientSize.Width-4); List.Columns[0].Width=w*46/100; List.Columns[1].Width=w*10/100; List.Columns[2].Width=w*18/100; List.Columns[3].Width=w-List.Columns[0].Width-List.Columns[1].Width-List.Columns[2].Width; };
        List.ItemActivate+=async(_,_)=> { if(List.SelectedItems.Count==1) await Open(List.SelectedItems[0]); };
        List.KeyDown+=async(_,e)=> { if(e.KeyCode==Keys.Back) { e.Handled=true; await Up(); } };
        List.SelectedIndexChanged+=(_,_)=>Summarize();
        List.GotFocus+=(_,_)=>Activated?.Invoke(this);
        // Every drive of this PC in one list, as a menu: plain combo box lists failed to open on some PCs.
        driveMenu.FlatAppearance.BorderColor=Color.FromArgb(160,172,190);
        driveMenu.Click+=(_,_)=>drivesList.Show(driveMenu,new Point(0,driveMenu.Height));
        Disposed+=(_,_)=> { drivesList.Dispose(); bold?.Dispose(); };
        SetActive(false);
    }
    public void SetActive(bool active) { header.BackColor=active?Brand.Blue:Color.FromArgb(226,232,242); header.ForeColor=active?Color.White:Brand.Ink; }
    /// <summary>The selected files and folders (".." left out), with full paths on this pane's PC.</summary>
    public IReadOnlyList<(string Path,bool Folder,long Size)> Selected=>List.SelectedItems.Cast<ListViewItem>().Where(i=>i.Tag is FileEntry).Select(i=>(System.IO.Path.Combine(Folder,((FileEntry)i.Tag!).Name),((FileEntry)i.Tag!).Folder,((FileEntry)i.Tag!).Size)).ToArray();
    public async Task Start() { await LoadDrives(); await Navigate(source.Home.Length>0?source.Home:""); }
    async Task LoadDrives()
    {
        try { driveList=await source.Drives(); } catch(Exception e) when(Expected(e)) { Fail(e); return; }
        drives.Controls.Clear(); drives.Controls.Add(driveMenu); drivesList.Items.Clear();
        foreach(var drive in driveList)
        {
            var item=new ToolStripMenuItem($"{drive.Root[..2]}   [{(drive.Label.Length>0?drive.Label:"_none_")}]   {SizeText(drive.Free)} free of {SizeText(drive.Total)}") {Tag=drive};
            item.Click+=async(_,_)=>await Navigate(drive.Root);
            drivesList.Items.Add(item);
        }
        foreach(var drive in driveList)
        {
            var button=new Button {Text=drive.Root[..2].ToLowerInvariant(),Width=46,Height=26,Margin=new(0,0,4,0),FlatStyle=FlatStyle.Flat,Font=new("Segoe UI",9),ImageList=ShellIcons.Images,ImageKey=ShellIcons.Key("x",true),TextImageRelation=TextImageRelation.ImageBeforeText,Tag=drive};
            button.FlatAppearance.BorderColor=Color.FromArgb(200,208,220);
            button.Click+=async(_,_)=>await Navigate(drive.Root);
            drives.Controls.Add(button);
        }
        ShowSpace();
    }
    public async Task Navigate(string target,string? select=null)
    {
        List.Enabled=false; summary.Text="Opening…";
        try
        {
            var (folder,entries)=await source.List(target);
            Folder=folder; shown=entries;
            path.Text=System.IO.Path.Combine(folder,"*.*");
            List.BeginUpdate(); List.Items.Clear();
            if(System.IO.Path.GetDirectoryName(folder)!=null) List.Items.Add(new ListViewItem(["..","","<DIR>",""]) {ImageKey=ShellIcons.Key("..",true)});
            foreach(var entry in entries)
            {
                string ext=entry.Folder?"":System.IO.Path.GetExtension(entry.Name).TrimStart('.');
                string stem=entry.Folder||ext.Length==0?entry.Name:entry.Name[..^(ext.Length+1)];
                var item=new ListViewItem([stem,ext,entry.Folder?"<DIR>":entry.Size.ToString("N0"),entry.Modified.ToLocalTime().ToString("MM/dd/yyyy HH:mm")]) {Tag=entry,ImageKey=ShellIcons.Key(entry.Name,entry.Folder)};
                if(entry.Folder) item.Font=bold??=new Font(List.Font,FontStyle.Bold);
                if(select!=null && entry.Name.Equals(select,StringComparison.OrdinalIgnoreCase)) item.Selected=true;
                List.Items.Add(item);
            }
            List.EndUpdate();
            if(List.Items.Count>0) { var focus=List.SelectedItems.Count>0?List.SelectedItems[0]:List.Items[0]; focus.Focused=true; focus.EnsureVisible(); }
            ShowSpace(); Summarize();
        }
        catch(Exception e) when(Expected(e)) { Fail(e); }
        finally { List.Enabled=true; }
    }
    public Task Reload()=>Navigate(Folder);
    public async Task ReloadAll() { if(source is RemoteFiles remote) remote.ForgetDrives(); await LoadDrives(); await Reload(); }
    async Task Open(ListViewItem item)
    {
        if(item.Tag is FileEntry {Folder:true} entry) await Navigate(System.IO.Path.Combine(Folder,entry.Name));
        else if(item.Tag==null) await Up();
    }
    async Task Up() { if(System.IO.Path.GetDirectoryName(Folder) is {} parent) await Navigate(parent,System.IO.Path.GetFileName(Folder)); }
    public async Task NewFolder(string name)
    {
        try { string made=await source.MakeFolder(Folder,name); await Navigate(Folder,System.IO.Path.GetFileName(made)); }
        catch(Exception e) when(Expected(e)) { Fail(e); }
    }
    void ShowSpace()
    {
        var drive=driveList.FirstOrDefault(d=>Folder.StartsWith(d.Root,StringComparison.OrdinalIgnoreCase));
        foreach(Button b in drives.Controls) b.BackColor=b.Tag is DriveEntry d && d==drive?Color.FromArgb(214,228,255):Color.White;
        driveMenu.Text=(drive?.Root[..2].ToLowerInvariant()??"--")+"  ▾"; driveMenu.BackColor=Color.White;
        foreach(ToolStripMenuItem item in drivesList.Items) item.Checked=item.Tag is DriveEntry d && d==drive;
        space.Text=drive==null?"":$"[{(drive.Label.Length>0?drive.Label:"_none_")}]  {SizeText(drive.Free)} of {SizeText(drive.Total)} free";
    }
    void Summarize()
    {
        var picked=List.SelectedItems.Cast<ListViewItem>().Select(i=>i.Tag).OfType<FileEntry>().ToArray();
        int files=shown.Count(e=>!e.Folder), dirs=shown.Count(e=>e.Folder);
        summary.ForeColor=Brand.Muted;
        summary.Text=$"{SizeText(picked.Where(e=>!e.Folder).Sum(e=>e.Size))} / {SizeText(shown.Where(e=>!e.Folder).Sum(e=>e.Size))} in {picked.Count(e=>!e.Folder)} / {files} file(s), {picked.Count(e=>e.Folder)} / {dirs} dir(s)";
    }
    void Fail(Exception e) { summary.ForeColor=Color.Firebrick; summary.Text=e is InvalidOperationException or UnauthorizedAccessException?e.Message:e is IOException?(source is RemoteFiles?"Lost the connection to the other PC. Press Ctrl+R to try again.":"Cannot open that folder."):e.Message; }
    static bool Expected(Exception e)=>e is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException or AuthenticationException or ArgumentException or OperationCanceledException;
    public static string SizeText(long bytes)=>bytes switch { <1024=>$"{bytes} b", <1024*1024=>$"{bytes/1024d:F1} k", <1024L*1024*1024=>$"{bytes/1048576d:F1} M", _=>$"{bytes/1073741824d:F1} G" };
}
/// <summary>Two panes, this PC on the left and the other PC on the right, with copy in both directions (F5), new folder (F7), refresh and the AI helper (F9).</summary>
public sealed class FileManagerWindow : Form
{
    readonly CancellationTokenSource lifetime=new();
    readonly FilePane local, remote;
    readonly RemoteFiles remoteFiles;
    readonly TransferStrip transfers;
    readonly AssistantPanel assistant;
    FilePane active;
    /// <summary>Starts the last copy again with more names left out, for the helper's "copy again without node_modules".</summary>
    Func<IReadOnlyCollection<string>,Task>? again;
    public FileManagerWindow(string remoteName,Func<CancellationToken,Task<Connection>> openFiles,Action<TransferStrip>? observeTransfers=null)
    {
        Text="HearthLink • File transfer • "+remoteName; ClientSize=new(1240,760); MinimumSize=new(900,520); Icon=Brand.AppIcon; Font=new("Segoe UI",10); BackColor=Brand.Canvas; KeyPreview=true; StartPosition=FormStartPosition.CenterParent;
        remoteFiles=new RemoteFiles(openFiles,lifetime.Token);
        local=new FilePane("Local • "+Environment.MachineName,new LocalFiles());
        remote=new FilePane("Remote • "+remoteName,remoteFiles);
        active=local;
        transfers=new TransferStrip(openFiles);
        observeTransfers?.Invoke(transfers);
        transfers.Finished+=async()=> { await local.ReloadAll(); await remote.ReloadAll(); };
        assistant=new AssistantPanel(Describe,(answer,request)=>TransferAssistant.ReadPlan(answer,local.Entries,remote.Entries,request,Names(local)),Run,plan=>TransferAssistant.Explain(plan,local.Folder,remote.Folder));
        transfers.Started+=batch=>assistant.Watch(batch,again==null?null:Again);
        transfers.Ended+=async(batch,message,failed)=>await assistant.Finished(batch,message,failed);
        var toRemote=Brand.Style(new Button {Text="→",Width=44,Height=40,Font=new("Segoe UI",14,FontStyle.Bold)},true);
        var toLocal=Brand.Style(new Button {Text="←",Width=44,Height=40,Font=new("Segoe UI",14,FontStyle.Bold)},true);
        var tips=new ToolTip(); tips.SetToolTip(toRemote,"Copy selected local files to the remote folder (F5)"); tips.SetToolTip(toLocal,"Copy selected remote files to the local folder (F5)");
        toRemote.Click+=async(_,_)=>await Copy(local); toLocal.Click+=async(_,_)=>await Copy(remote);
        var middle=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,Padding=new(4,120,4,0),WrapContents=false};
        middle.Controls.AddRange([toRemote,toLocal]);
        var panes=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Padding=new(8,8,8,4)};
        panes.ColumnStyles.Add(new(SizeType.Percent,50)); panes.ColumnStyles.Add(new(SizeType.Absolute,56)); panes.ColumnStyles.Add(new(SizeType.Percent,50));
        panes.RowStyles.Add(new(SizeType.Percent,100));
        panes.Controls.Add(local,0,0); panes.Controls.Add(middle,1,0); panes.Controls.Add(remote,2,0);
        var keys=new TableLayoutPanel {Dock=DockStyle.Bottom,Height=40,ColumnCount=5,RowCount=1,Padding=new(8,2,8,4)};
        for(int i=0;i<5;i++) keys.ColumnStyles.Add(new(SizeType.Percent,20));
        Button Key(string label,Func<Task> action) { var b=Brand.Style(new Button {Text=label,Dock=DockStyle.Fill,Margin=new(2)}); b.Click+=async(_,_)=>await action(); return b; }
        keys.Controls.Add(Key("F5 Copy",()=>Copy(active)),0,0);
        keys.Controls.Add(Key("F7 New Folder",NewFolder),1,0);
        keys.Controls.Add(Key("Ctrl+R Refresh",ReloadBoth),2,0);
        keys.Controls.Add(Key("F9 AI helper",ToggleHelper),3,0);
        keys.Controls.Add(Key("Esc Close",()=> { Close(); return Task.CompletedTask; }),4,0);
        Controls.Add(panes); Controls.Add(assistant); Controls.Add(transfers); Controls.Add(keys);
        foreach(var pane in new[]{local,remote}) pane.Activated+=p=> { active=p; local.SetActive(p==local); remote.SetActive(p==remote); };
        KeyDown+=async(_,e)=>
        {
            if(e.KeyCode==Keys.F5) { e.Handled=true; await Copy(active); }
            else if(e.KeyCode==Keys.F7) { e.Handled=true; await NewFolder(); }
            else if(e.KeyCode==Keys.F9) { e.Handled=true; await ToggleHelper(); }
            else if(e.KeyCode==Keys.R && e.Control) { e.Handled=true; await ReloadBoth(); }
            else if(e.KeyCode==Keys.Tab && !e.Control) { e.Handled=true; (active==local?remote:local).List.Focus(); }
            else if(e.KeyCode==Keys.Escape && !assistant.ContainsFocus) { e.Handled=true; Close(); }
        };
        Shown+=async(_,_)=> { local.List.Focus(); await Task.WhenAll(local.Start(),remote.Start(),assistant.Connect()); };
        FormClosed+=(_,_)=> { transfers.Cancel(); lifetime.Cancel(); remoteFiles.Dispose(); lifetime.Dispose(); };
    }
    async Task ReloadBoth() { await local.ReloadAll(); await remote.ReloadAll(); }
    /// <summary>Copies the selection of <paramref name="from"/> into the folder open in the other pane, replacing same-named files.</summary>
    async Task Copy(FilePane from)
    {
        var picks=from.Selected;
        if(picks.Count==0) { transfers.Notice("Select files or folders to copy first."); return; }
        await Copy(from==local,picks,[]);
    }
    /// <summary>Copies <paramref name="picks"/> into the folder open on the other side, leaving <paramref name="skip"/> out inside folders.</summary>
    async Task Copy(bool toRemote,IReadOnlyList<(string Path,bool Folder,long Size)> picks,IReadOnlyCollection<string> skip)
    {
        string target=toRemote?remote.Folder:local.Folder;
        if(target.Length==0) return;
        if(transfers.Busy) { transfers.Notice("A transfer is already running. Wait or cancel it first."); return; }
        again=more=>Copy(toRemote,picks,skip.Union(more,StringComparer.OrdinalIgnoreCase).ToArray());
        if(toRemote) await transfers.Send(picks.Select(p=>p.Path).ToArray(),target,skip);
        else await transfers.Get(picks,target,skip);
    }
    async Task Again(IReadOnlyList<string> without)
    {
        var redo=again;
        if(redo==null) return;
        transfers.Cancel();
        while(transfers.Busy) await Task.Delay(100);
        await redo(without);
    }
    /// <summary>
    /// Runs a plan the helper proposed and the user accepted; its items were already matched to the source folder. Sending
    /// items the user neither selected nor named needs one more confirmation, since folder names can try to steer the model.
    /// </summary>
    Task Run(TransferPlan plan)
    {
        if(plan.ToRemote && plan.Unasked.Count>0 && MessageBox.Show(this,
            $"The helper wants to SEND these to the other PC, but you did not select or name them:\n\n{string.Join("\n",plan.Unasked.Select(i=>"• "+i))}\n\nSend them to {remote.Folder}?",
            "Check before sending",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) return Task.CompletedTask;
        var source=plan.ToRemote?local:remote;
        var picks=plan.Items.Select(name=>source.Entries.FirstOrDefault(e=>e.Name==name)).OfType<FileEntry>().Select(e=>(Path.Combine(source.Folder,e.Name),e.Folder,e.Size)).ToArray();
        if(picks.Length==0) { transfers.Notice("Those items are no longer in the folder. Ask the helper again."); return Task.CompletedTask; }
        return Copy(plan.ToRemote,picks,plan.Skip);
    }
    IReadOnlyList<AiMessage> Describe(string request)=>
    [
        new("system",TransferAssistant.Instructions),
        new("user",TransferAssistant.Describe(request,local.Folder,local.Entries,Names(local),remote.Folder,remote.Entries,Names(remote))),
    ];
    static string[] Names(FilePane pane)=>pane.Selected.Select(p=>Path.GetFileName(p.Path)).ToArray();
    Task ToggleHelper()
    {
        assistant.Visible=!assistant.Visible;
        if(assistant.Visible) assistant.Focus(); else active.List.Focus();
        return Task.CompletedTask;
    }
    async Task NewFolder()
    {
        using var dialog=new Form {Text="New folder",ClientSize=new(400,130),FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false,StartPosition=FormStartPosition.CenterParent,Font=Font,BackColor=Brand.Canvas};
        var box=new TextBox {Left=16,Top=40,Width=368,Text="New folder"}; var ok=Brand.Style(new Button {Text="Create",Left=284,Top=80,Width=100,DialogResult=DialogResult.OK},true);
        dialog.Controls.AddRange([new Label {Text="Folder name in "+(active==local?"Local":"Remote"),Left=16,Top=12,Width=368},box,ok]); dialog.AcceptButton=ok;
        if(dialog.ShowDialog(this)!=DialogResult.OK) return;
        if(!FilePolicy.ValidName(box.Text.Trim())) { transfers.Notice("That folder name is not allowed."); return; }
        await active.NewFolder(box.Text.Trim());
    }
}
