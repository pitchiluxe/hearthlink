using HearthLink.Core;
using System.Security.Authentication;

namespace HearthLink;
/// <summary>The bottom strip while files move: one batch at a time on its own files connection, with progress and Cancel.</summary>
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
    string openTarget=LocalFolder;
    public bool Busy => running!=null;
    /// <summary>Raised when a batch ends (finished, cancelled or failed), so file lists can refresh.</summary>
    public event Action? Finished;
    public TransferStrip(Func<CancellationToken,Task<Connection>> openFiles)
    {
        open=openFiles; Dock=DockStyle.Bottom; Height=44; BackColor=Brand.Navy; Visible=false; Padding=new(0,6,8,6);
        Controls.AddRange([text,folder,progress,cancel]);
        cancel.Click+=(_,_)=>running?.Cancel();
        folder.LinkClicked+=(_,_)=> { Directory.CreateDirectory(openTarget); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",openTarget) {UseShellExecute=true}); };
        fade.Tick+=(_,_)=> { fade.Stop(); if(!Busy) Visible=false; };
        Disposed+=(_,_)=> { running?.Cancel(); fade.Dispose(); };
    }
    public void Cancel()=>running?.Cancel();
    /// <summary>One thing to send: a file (Local set) or a folder to create (Local null). Root numbers the dropped folder it belongs to (-1 for a loose file); Relative is its path inside that folder.</summary>
    public sealed record Outgoing(string? Local,int Root,string RootName,string Relative,long Size);
    /// <summary>One thing to get: a host file (Remote set) or a folder to create here (Remote null), numbered and placed like <see cref="Outgoing"/>.</summary>
    public sealed record Incoming(string? Remote,int Root,string RootName,string Relative,long Size);
    static string Inner(string relative,string name)=>relative.Length==0?name:relative+"\\"+name;
    static bool Fits(string rootName,string relative,string name)=>FilePolicy.ValidName(name) && Inner(relative,name).Split('\\').Length<FileNames.MaxDepth && Inner(relative,name).Length+rootName.Length<FileNames.MaxRelativeLength;
    /// <summary>
    /// Turns local paths (dropped or picked) into what to send, in order: each folder before its contents, then its files, then
    /// its subfolders. Linked folders (junctions) are not followed, and names the other PC would refuse are left out.
    /// </summary>
    public static IReadOnlyList<Outgoing> Expand(IEnumerable<string> paths)
    {
        var items=new List<Outgoing>(); int roots=0;
        void Walk(string folder,int root,string rootName,string relative)
        {
            items.Add(new(null,root,rootName,relative,0));
            var options=new EnumerationOptions {IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint|FileAttributes.System};
            foreach(var file in new DirectoryInfo(folder).EnumerateFiles("*",options).Where(f=>Fits(rootName,relative,f.Name)).OrderBy(f=>f.Name,StringComparer.OrdinalIgnoreCase))
                items.Add(new(file.FullName,root,rootName,Inner(relative,file.Name),file.Length));
            foreach(var sub in new DirectoryInfo(folder).EnumerateDirectories("*",options).Where(d=>Fits(rootName,relative,d.Name)).OrderBy(d=>d.Name,StringComparer.OrdinalIgnoreCase))
                Walk(sub.FullName,root,rootName,Inner(relative,sub.Name));
        }
        foreach(string path in paths)
        {
            if(File.Exists(path) && FilePolicy.ValidName(Path.GetFileName(path))) items.Add(new(path,-1,"",Path.GetFileName(path),new FileInfo(path).Length));
            else if(Directory.Exists(path) && Path.GetFileName(path.TrimEnd('\\')) is {} name && FilePolicy.ValidName(name)) Walk(path,roots++,name,"");
        }
        return items;
    }
    /// <summary>The same expansion for files and folders picked on the other PC, walking its folders with <see cref="FileClient.List"/>.</summary>
    public static async Task<IReadOnlyList<Incoming>> ExpandRemote(FileClient client,IEnumerable<(string Path,bool Folder,long Size)> picks,CancellationToken token)
    {
        var items=new List<Incoming>(); int roots=0;
        async Task Walk(string folder,int root,string rootName,string relative)
        {
            items.Add(new(null,root,rootName,relative,0));
            var listing=await client.List(folder,token);
            foreach(var file in listing.Entries.Where(e=>!e.Folder && Fits(rootName,relative,e.Name)))
                items.Add(new(Path.Combine(listing.Folder,file.Name),root,rootName,Inner(relative,file.Name),file.Size));
            foreach(var sub in listing.Entries.Where(e=>e.Folder && Fits(rootName,relative,e.Name)))
                await Walk(Path.Combine(listing.Folder,sub.Name),root,rootName,Inner(relative,sub.Name));
        }
        foreach(var (path,isFolder,size) in picks)
        {
            string name=Path.GetFileName(path.TrimEnd('\\'));
            if(!FilePolicy.ValidName(name)) continue;
            if(isFolder) await Walk(path,roots++,name,""); else items.Add(new(path,-1,"",name,size));
        }
        return items;
    }
    /// <summary>Shows a short message in the strip for 10 s.</summary>
    public void Notice(string message) { text.Text=message; folder.Visible=false; cancel.Visible=false; progress.Visible=false; Visible=true; BringToFront(); fade.Stop(); fade.Start(); }
    /// <summary>Sends local files and folders into <paramref name="remoteFolder"/> on the other PC, or its Downloads\HearthLink when null.</summary>
    public Task Send(IReadOnlyList<string> paths,string? remoteFolder=null)=>Send(Expand(paths),remoteFolder);
    public Task Send(IReadOnlyList<Outgoing> items,string? remoteFolder=null)
    {
        // The host may rename a top folder ("Album (2)"); later items of that folder go under the name it chose.
        var placed=new Dictionary<int,string>();
        string Remote(Outgoing o)=>o.Root<0?o.Relative:o.Relative.Length==0?placed[o.Root]:placed[o.Root]+"\\"+o.Relative;
        string where=remoteFolder??"Downloads\\HearthLink";
        return Run("Sending",(_,_)=>Task.FromResult<IReadOnlyList<Job>>(items.Select(o=>new Job(o.Local==null?"folder "+(o.Relative.Length==0?o.RootName:o.RootName+"\\"+o.Relative):Path.GetFileName(o.Local),o.Size,o.Local!=null,async(client,report,token)=>
        {
            if(o.Local==null && o.Relative.Length==0) placed[o.Root]=await client.Folder(o.RootName,token,remoteFolder);
            else if(o.Local==null) await client.Folder(Remote(o),token,remoteFolder);
            else await client.Put(o.Local,report,token,o.Root<0?null:Remote(o),remoteFolder);
        })).ToArray()),files=>$"Sent {Files(files)} to {where} on the other PC",null);
    }
    /// <summary>Copies files and folders picked on the other PC into <paramref name="localFolder"/>, or this PC's Downloads\HearthLink when null.</summary>
    public Task Get(IReadOnlyList<(string Path,bool Folder,long Size)> picks,string? localFolder=null)
    {
        string target=localFolder??LocalFolder;
        var placed=new Dictionary<int,string>();
        string Local(Incoming i)=>i.Relative.Length==0?placed[i.Root]:Path.Combine([placed[i.Root],..FileNames.SplitRelative(i.Relative)]);
        return Run("Getting",async(client,token)=>(await ExpandRemote(client,picks,token)).Select(i=>new Job(i.Remote==null?"folder "+(i.Relative.Length==0?i.RootName:i.RootName+"\\"+i.Relative):Path.GetFileName(i.Remote),i.Size,i.Remote!=null,async(client,report,token)=>
        {
            Directory.CreateDirectory(target);
            if(i.Remote==null && i.Relative.Length==0) { placed[i.Root]=FileNames.Unique(target,i.RootName); Directory.CreateDirectory(placed[i.Root]); }
            else if(i.Remote==null) Directory.CreateDirectory(Local(i));
            else await client.Get(i.Remote,i.Root<0?target:Path.GetDirectoryName(Local(i))!,report,token);
        })).ToArray(),files=>$"Saved {Files(files)} to {target}",target);
    }
    static string Files(int count)=>count==1?"1 file":$"{count} files";
    sealed record Job(string Name,long Size,bool IsFile,Func<FileClient,IProgress<long>,CancellationToken,Task> Do);
    async Task Run(string verb,Func<FileClient,CancellationToken,Task<IReadOnlyList<Job>>> prepare,Func<int,string> done,string? opens)
    {
        if(running!=null) { Notice("A transfer is already running. Wait or cancel it first."); return; }
        using var cts=new CancellationTokenSource(); running=cts;
        fade.Stop(); folder.Visible=false; cancel.Visible=true; progress.Visible=true; Visible=true; BringToFront();
        text.Text=$"{verb}: preparing…"; progress.Value=0;
        int files=0;
        try
        {
            using var client=new FileClient(await open(cts.Token));
            var jobs=await prepare(client,cts.Token);
            for(int i=0;i<jobs.Count;i++)
            {
                var job=jobs[i]; int index=i;
                void Show(long bytes) { int share=job.Size<=0?1000:(int)Math.Clamp(bytes*1000/job.Size,0,1000); progress.Value=share; text.Text=$"{verb} {job.Name} • {share/10}% • {index+1} of {jobs.Count}"; }
                Show(0);
                await job.Do(client,new Progress<long>(Show),cts.Token);
                if(job.IsFile) files++;
            }
            text.Text=done(files);
            if(opens!=null) { openTarget=opens; folder.Visible=true; }
        }
        catch(OperationCanceledException) { text.Text=files>0?$"Transfer stopped after {Files(files)}":"Transfer stopped"; }
        catch(Exception e) when(e is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException or AuthenticationException or ArgumentException)
        {
            text.Text="Transfer stopped: "+(e is IOException && e.GetType()!=typeof(FileNotFoundException)?"the connection to the other PC was lost.":e.Message);
        }
        finally { running=null; cancel.Visible=false; progress.Visible=false; fade.Start(); Finished?.Invoke(); }
    }
}
