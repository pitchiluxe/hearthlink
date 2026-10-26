using HearthLink.Core;
using System.Security.Authentication;

namespace HearthLink;
/// <summary>Browses the other PC's folders on its own files connection, starting at its user folder, and hands chosen files to the viewer's transfer strip.</summary>
public sealed class RemoteFilesWindow : Form
{
    readonly Func<CancellationToken,Task<Connection>> open;
    readonly Func<IReadOnlyList<(string Path,long Size)>,Task> download;
    readonly CancellationTokenSource lifetime=new();
    readonly ListView list=new() {View=View.Details,FullRowSelect=true,Dock=DockStyle.Fill,MultiSelect=true,HideSelection=false};
    readonly ComboBox drives=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=90,Margin=new(0,4,8,0)};
    readonly Label path=new() {AutoSize=false,Width=520,Height=30,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,ForeColor=Brand.Ink};
    readonly Label status=new() {Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Brand.Muted};
    readonly Button up=Brand.Style(new Button {Text="Up",AutoSize=true});
    readonly Button get=Brand.Style(new Button {Text="Download",AutoSize=true,Dock=DockStyle.Right},true);
    FileClient? client;
    string folder="";
    public RemoteFilesWindow(string remoteName,Func<CancellationToken,Task<Connection>> openFiles,Func<IReadOnlyList<(string Path,long Size)>,Task> downloadFiles)
    {
        open=openFiles; download=downloadFiles;
        Text="Files on "+remoteName; ClientSize=new(760,520); Icon=Brand.AppIcon; Font=new("Segoe UI",10); BackColor=Brand.Canvas; StartPosition=FormStartPosition.CenterParent;
        list.Columns.Add("Name",400); list.Columns.Add("Size",110); list.Columns.Add("Modified",180);
        var top=new FlowLayoutPanel {Dock=DockStyle.Top,Height=44,Padding=new(10,6,10,0),WrapContents=false};
        top.Controls.AddRange([up,drives,path]);
        var bottom=new Panel {Dock=DockStyle.Bottom,Height=48,Padding=new(10,8,10,8)};
        bottom.Controls.AddRange([status,get]);
        Controls.Add(list); Controls.Add(top); Controls.Add(bottom);
        up.Click+=async(_,_)=> { if(Path.GetDirectoryName(folder) is {} parent) await Navigate(parent); };
        drives.SelectionChangeCommitted+=async(_,_)=> { if(drives.SelectedItem is string root) await Navigate(root); };
        list.ItemActivate+=async(_,_)=> { if(list.SelectedItems.Count==1 && list.SelectedItems[0].Tag is FileEntry {Folder:true} entry) await Navigate(Path.Combine(folder,entry.Name)); };
        get.Click+=async(_,_)=>
        {
            var chosen=list.SelectedItems.Cast<ListViewItem>().Select(i=>(FileEntry)i.Tag!).Where(e=>!e.Folder).Select(e=>(Path.Combine(folder,e.Name),e.Size)).ToArray();
            if(chosen.Length==0) { status.Text="Select one or more files (folders cannot be downloaded yet)."; return; }
            status.Text=$"Downloading {chosen.Length} file(s) • see the viewer for progress";
            await download(chosen);
        };
        Shown+=async(_,_)=>
        {
            try { client=new FileClient(await open(lifetime.Token)); await Navigate(""); }
            catch(Exception e) when(e is IOException or InvalidOperationException or AuthenticationException or OperationCanceledException or ArgumentException) { Lost(e); }
        };
        FormClosed+=(_,_)=> { lifetime.Cancel(); client?.Dispose(); lifetime.Dispose(); };
    }
    async Task Navigate(string target)
    {
        if(client==null) return;
        UseWaitCursor=true; list.Enabled=false; status.Text="Opening…";
        try
        {
            var listing=await client.List(target,lifetime.Token);
            folder=listing.Folder; path.Text=folder;
            if(listing.Drives.Length>0) { drives.Items.Clear(); drives.Items.AddRange(listing.Drives); }
            drives.SelectedItem=drives.Items.Cast<string>().FirstOrDefault(d=>string.Equals(d,Path.GetPathRoot(folder),StringComparison.OrdinalIgnoreCase));
            list.BeginUpdate(); list.Items.Clear();
            foreach(var entry in listing.Entries) list.Items.Add(new ListViewItem([entry.Name,entry.Folder?"Folder":SizeText(entry.Size),entry.Modified.ToLocalTime().ToString("g")]) {Tag=entry});
            list.EndUpdate();
            up.Enabled=Path.GetDirectoryName(folder)!=null;
            status.Text=listing.Entries.Count==1?"1 item":$"{listing.Entries.Count} items";
        }
        catch(InvalidOperationException e) { status.Text=e.Message; }
        catch(Exception e) when(e is IOException or InvalidDataException or OperationCanceledException) { Lost(e); }
        finally { UseWaitCursor=false; list.Enabled=client!=null; }
    }
    void Lost(Exception e)
    {
        client?.Dispose(); client=null; list.Enabled=false; get.Enabled=false; up.Enabled=false;
        status.Text=e is InvalidOperationException ? e.Message : "Connection to the other PC was lost. Close this window and try again.";
    }
    static string SizeText(long bytes)=>bytes switch { <1024=>$"{bytes} B", <1024*1024=>$"{bytes/1024d:F0} KB", <1024L*1024*1024=>$"{bytes/1048576d:F1} MB", _=>$"{bytes/1073741824d:F2} GB" };
}
