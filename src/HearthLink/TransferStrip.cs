using HearthLink.Core;
using System.Security.Authentication;

namespace HearthLink;
/// <summary>The viewer's bottom strip while files move: one batch at a time on its own files connection, with progress and Cancel.</summary>
public sealed class TransferStrip : Panel
{
    public static string LocalFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads","HearthLink");
    readonly Func<CancellationToken,Task<Connection>> open;
    readonly Label text=new() {Dock=DockStyle.Fill,ForeColor=Color.White,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,Padding=new(12,0,0,0)};
    readonly ProgressBar progress=new() {Dock=DockStyle.Right,Width=220,Maximum=1000};
    readonly Button cancel=Brand.Style(new Button {Text="Cancel",Dock=DockStyle.Right,Width=96});
    readonly LinkLabel folder=new() {Text="Open folder",Dock=DockStyle.Right,Width=110,TextAlign=ContentAlignment.MiddleCenter,LinkColor=Color.White,Visible=false};
    readonly System.Windows.Forms.Timer fade=new() {Interval=10000};
    CancellationTokenSource? running;
    public bool Busy => running!=null;
    public TransferStrip(Func<CancellationToken,Task<Connection>> openFiles)
    {
        open=openFiles; Dock=DockStyle.Bottom; Height=44; BackColor=Brand.Navy; Visible=false; Padding=new(0,6,8,6);
        Controls.AddRange([text,folder,progress,cancel]);
        cancel.Click+=(_,_)=>running?.Cancel();
        folder.LinkClicked+=(_,_)=> { Directory.CreateDirectory(LocalFolder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",LocalFolder) {UseShellExecute=true}); };
        fade.Tick+=(_,_)=> { fade.Stop(); if(!Busy) Visible=false; };
        Disposed+=(_,_)=> { running?.Cancel(); fade.Dispose(); };
    }
    public void Cancel()=>running?.Cancel();
    /// <summary>One thing to send: a file (Local set) or a folder to create (Local null). Root numbers the dropped folder it belongs to (-1 for a loose file); Relative is its path inside that folder.</summary>
    public sealed record Outgoing(string? Local,int Root,string RootName,string Relative,long Size);
    public const int MaxItems=20000;
    /// <summary>
    /// Turns paths dropped from Explorer into what to send, in order: each folder before its contents, then its files, then its
    /// subfolders. Linked folders (junctions) are not followed, and names the other PC would refuse are left out.
    /// </summary>
    public static IReadOnlyList<Outgoing> Expand(IEnumerable<string> paths)
    {
        var items=new List<Outgoing>(); int roots=0;
        void Walk(string folder,int root,string rootName,string relative)
        {
            if(items.Count>=MaxItems) return;
            items.Add(new(null,root,rootName,relative,0));
            var options=new EnumerationOptions {IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint|FileAttributes.System};
            string Inner(string name)=>relative.Length==0?name:relative+"\\"+name;
            bool Fits(string name)=>FilePolicy.ValidName(name) && Inner(name).Split('\\').Length<FileNames.MaxDepth && Inner(name).Length+rootName.Length<FileNames.MaxRelativeLength;
            foreach(var file in new DirectoryInfo(folder).EnumerateFiles("*",options).Where(f=>Fits(f.Name)).OrderBy(f=>f.Name,StringComparer.OrdinalIgnoreCase))
                if(items.Count<MaxItems) items.Add(new(file.FullName,root,rootName,Inner(file.Name),file.Length));
            foreach(var sub in new DirectoryInfo(folder).EnumerateDirectories("*",options).Where(d=>Fits(d.Name)).OrderBy(d=>d.Name,StringComparer.OrdinalIgnoreCase))
                Walk(sub.FullName,root,rootName,Inner(sub.Name));
        }
        foreach(string path in paths)
        {
            if(File.Exists(path) && FilePolicy.ValidName(Path.GetFileName(path))) items.Add(new(path,-1,"",Path.GetFileName(path),new FileInfo(path).Length));
            else if(Directory.Exists(path) && Path.GetFileName(path.TrimEnd('\\')) is {} name && FilePolicy.ValidName(name)) Walk(path,roots++,name,"");
        }
        return items;
    }
    /// <summary>Shows a short message in the strip for 10 s.</summary>
    public void Notice(string message) { text.Text=message; folder.Visible=false; cancel.Visible=false; progress.Visible=false; Visible=true; BringToFront(); fade.Stop(); fade.Start(); }
    /// <summary>Sends local files and folders (see <see cref="Expand"/>) to the other PC's Downloads\HearthLink.</summary>
    public Task Send(IReadOnlyList<string> paths)=>Send(Expand(paths));
    public Task Send(IReadOnlyList<Outgoing> items)
    {
        // The host may rename a dropped folder ("Album (2)"); later items of that folder go under the name it chose.
        var placed=new Dictionary<int,string>();
        string Remote(Outgoing o)=>o.Root<0?o.Relative:o.Relative.Length==0?placed[o.Root]:placed[o.Root]+"\\"+o.Relative;
        return Run("Sending",items.Select(o=>(o.Local==null?"folder "+(o.Relative.Length==0?o.RootName:o.RootName+"\\"+o.Relative):Path.GetFileName(o.Local),o.Size)).ToArray(),async(client,i,report,token)=>
        {
            var o=items[i];
            if(o.Local==null && o.Relative.Length==0) placed[o.Root]=await client.Folder(o.RootName,token);
            else if(o.Local==null) await client.Folder(Remote(o),token);
            else await client.Put(o.Local,report,token,o.Root<0?null:Remote(o));
        },count=>$"Sent {Files(items.Take(count).Count(o=>o.Local!=null))} to Downloads\\HearthLink on the other PC",false);
    }
    /// <summary>Copies files from the other PC into this PC's Downloads\HearthLink.</summary>
    public Task Get(IReadOnlyList<(string Path,long Size)> files)=>Run("Getting",files.Select(f=>(Path.GetFileName(f.Path),f.Size)).ToArray(),(client,i,report,token)=>client.Get(files[i].Path,LocalFolder,report,token),
        count=>$"Saved {Files(count)} to Downloads\\HearthLink",true);
    static string Files(int count)=>count==1?"1 file":$"{count} files";
    async Task Run(string verb,(string Name,long Size)[] items,Func<FileClient,int,IProgress<long>,CancellationToken,Task> step,Func<int,string> done,bool local)
    {
        if(items.Length==0) return;
        if(running!=null) { text.Text="A transfer is already running. Wait or cancel it first."; return; }
        using var cts=new CancellationTokenSource(); running=cts;
        fade.Stop(); folder.Visible=false; cancel.Visible=true; progress.Visible=true; Visible=true; BringToFront();
        int finished=0;
        try
        {
            using var client=new FileClient(await open(cts.Token));
            for(int i=0;i<items.Length;i++)
            {
                var (name,size)=items[i]; int index=i;
                void Show(long bytes) { int share=size<=0?1000:(int)Math.Clamp(bytes*1000/size,0,1000); progress.Value=share; text.Text=$"{verb} {name} • {share/10}% • {index+1} of {items.Length}"; }
                Show(0);
                await step(client,i,new Progress<long>(Show),cts.Token); finished++;
            }
            text.Text=done(finished); folder.Visible=local;
        }
        catch(OperationCanceledException) { text.Text=finished>0?$"Transfer stopped after {Files(finished)}":"Transfer stopped"; }
        catch(Exception e) when(e is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException or AuthenticationException or ArgumentException)
        {
            text.Text="Transfer stopped: "+(e is IOException?"the connection to the other PC was lost.":e.Message);
        }
        finally { running=null; cancel.Visible=false; progress.Visible=false; fade.Start(); }
    }
}
