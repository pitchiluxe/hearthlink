using HearthLink.Core;
using System.Diagnostics;

namespace HearthLink;

public sealed class UpdateCenter : IDisposable
{
    readonly MainWindow owner;
    readonly IdentityStore identity;
    readonly CancellationTokenSource stop=new();
    readonly HttpClient http;
    readonly System.Windows.Forms.Timer daily=new() {Interval=24*60*60*1000};
    readonly NotifyIcon tray=new() {Icon=Brand.AppIcon,Text="HearthLink updates"};
    UpdateWindow? window;
    AppRelease? available;
    string status="Check for a newer version of HearthLink.";
    bool checking;
    public event Action<Version?>? AvailabilityChanged;
    public static Version Installed {get;}=new(typeof(MainWindow).Assembly.GetName().Version!.ToString(3));
    public UpdateCenter(MainWindow owner,IdentityStore identity,HttpClient? client=null)
    {
        this.owner=owner; this.identity=identity; http=client??AppUpdates.CreateClient();
        daily.Tick+=async(_,_)=> {if(identity.Config.CheckUpdates) await Check(false);};
        tray.BalloonTipClicked+=(_,_)=>Show(); tray.DoubleClick+=(_,_)=>Show();
    }
    public async void Start()
    {
        daily.Start();
        try {await Task.Delay(TimeSpan.FromSeconds(8),stop.Token); if(identity.Config.CheckUpdates) await Check(false);}
        catch(OperationCanceledException) {}
    }
    public void Show()
    {
        if(window is {IsDisposed:false}) {window.Activate(); return;}
        window=new(owner,identity,new AppUpdates(http),stop.Token,()=>Check(true));
        window.Show(owner); Refresh();
    }
    void Refresh()=>window?.SetRelease(available,status,checking);
    public async Task Check(bool manual)
    {
        if(checking) return; checking=true; status="Checking GitHub for updates…"; Refresh();
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
            available=await new AppUpdates(http).CheckAsync(Installed,timeout.Token);
            AvailabilityChanged?.Invoke(available?.Version);
            status=available==null?"You have the latest published version.":$"HearthLink {available.Version} is ready to download.";
            if(available!=null && !manual)
            {
                Show(); tray.Visible=true;
                tray.ShowBalloonTip(8000,"HearthLink update available",$"Version {available.Version} is available. Open Updates to download it.",ToolTipIcon.Info);
            }
        }
        catch(Exception e) when(e is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {status=e is HttpRequestException {StatusCode:System.Net.HttpStatusCode.NotFound}?"No public release is available yet. Try again later.":"Could not check for updates. Check your internet connection and try again.";}
        finally {checking=false; if(!stop.IsCancellationRequested) Refresh();}
    }
    public void Dispose() {stop.Cancel(); daily.Dispose(); tray.Visible=false; tray.Dispose(); window?.Close(); http.Dispose(); stop.Dispose();}
}

sealed class UpdateWindow : Form
{
    readonly MainWindow owner;
    readonly AppUpdates updates;
    readonly CancellationToken lifetime;
    readonly Label description=new() {Dock=DockStyle.Top,Height=65,ForeColor=Brand.Muted};
    readonly Button download=Brand.Style(new Button {Text="Download update",AutoSize=true},true);
    readonly Button check=Brand.Style(new Button {Text="Check now",AutoSize=true});
    readonly Button cancel=Brand.Style(new Button {Text="Cancel download",AutoSize=true,Enabled=false});
    readonly ProgressBar progress=new() {Dock=DockStyle.Top,Height=18};
    AppRelease? release;
    CancellationTokenSource? transfer;
    string? installer;
    public UpdateWindow(MainWindow owner,IdentityStore identity,AppUpdates updates,CancellationToken lifetime,Func<Task> onCheck)
    {
        this.owner=owner; this.updates=updates; this.lifetime=lifetime;
        Text="HearthLink • Updates"; Icon=Brand.AppIcon; ClientSize=new(540,330); MinimumSize=Size; MaximumSize=Size;
        StartPosition=FormStartPosition.CenterParent; BackColor=Brand.Canvas; ForeColor=Brand.Ink; Font=new("Segoe UI",10); Padding=new(26);
        var heading=new Label {Text="Keep HearthLink up to date",Font=new("Segoe UI",19,FontStyle.Bold),Dock=DockStyle.Top,Height=42};
        var version=new Label {Text=$"Installed version {UpdateCenter.Installed} • Windows x64",Dock=DockStyle.Top,Height=36};
        var auto=new CheckBox {Text="Automatically check at launch and daily",Checked=identity.Config.CheckUpdates,AutoSize=true};
        auto.CheckedChanged+=(_,_)=> {identity.Config.CheckUpdates=auto.Checked; identity.Save();};
        var options=new FlowLayoutPanel {Dock=DockStyle.Bottom,Height=40}; options.Controls.Add(auto);
        var actions=new FlowLayoutPanel {Dock=DockStyle.Bottom,Height=48,WrapContents=false}; actions.Controls.AddRange([download,check,cancel]);
        check.Click+=async(_,_)=>await onCheck(); cancel.Click+=(_,_)=>transfer?.Cancel();
        download.Click+=async(_,_)=>await DownloadOrInstall();
        Controls.Add(progress); Controls.Add(description); Controls.Add(version); Controls.Add(heading); Controls.Add(actions); Controls.Add(options);
        FormClosing+=(_,_)=>transfer?.Cancel();
    }
    public void SetRelease(AppRelease? value,string text,bool checking)
    {
        if(IsDisposed || transfer!=null) return;
        if(value!=release) {installer=null; download.Text="Download update"; progress.Value=0;}
        release=value; description.Text=text; check.Enabled=!checking; download.Enabled=value!=null && !checking;
    }
    async Task DownloadOrInstall()
    {
        if(release==null) return;
        if(installer!=null)
        {
            if(owner.HasRemoteSession) {MessageBox.Show(this,"End your remote session before installing the update.","HearthLink"); return;}
            if(MessageBox.Show(this,"Close HearthLink and start the update installer? Windows will ask for administrator permission. Your paired computers will be kept.","Install HearthLink update",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes) return;
            try {using var process=Process.Start(new ProcessStartInfo(installer) {UseShellExecute=true}); if(process!=null) owner.Close();}
            catch(System.ComponentModel.Win32Exception) {description.Text="Installation did not start. You can try again when ready.";}
            return;
        }
        transfer=CancellationTokenSource.CreateLinkedTokenSource(lifetime); transfer.CancelAfter(TimeSpan.FromMinutes(15));
        download.Enabled=false; check.Enabled=false; cancel.Enabled=true;
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HearthLink","Updates",Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); string path=Path.Combine(folder,$"HearthLink-{release.Version}-win-x64-setup.exe");
            description.Text="Downloading and verifying the installer…";
            await updates.DownloadAsync(release,path,new Progress<int>(value=> {if(!IsDisposed) progress.Value=value;}),transfer.Token);
            installer=path; description.Text="Download verified. Ready to install when your remote sessions are closed."; download.Text="Install update";
        }
        catch(Exception e) when(e is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        {if(!IsDisposed) description.Text=e is OperationCanceledException?"Download cancelled. Nothing was installed.":"Download could not be verified or saved. Nothing was installed. Try again.";}
        finally {transfer.Dispose(); transfer=null; if(!IsDisposed) {download.Enabled=true; check.Enabled=true; cancel.Enabled=false;}}
    }
}
