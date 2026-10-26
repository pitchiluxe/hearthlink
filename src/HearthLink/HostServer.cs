using HearthLink.Core;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;

namespace HearthLink;
public sealed class HostServer : IDisposable
{
    readonly IdentityStore identity;
    readonly MainWindow ui;
    readonly TcpListener listener;
    readonly CancellationTokenSource lifetime=new();
    readonly SemaphoreSlim clients=new(8,8), session=new(1,1);
    CancellationTokenSource? active;
    SessionFileGrant? files;
    public bool HasActiveSession=>active!=null;
    public bool Listening { get; private set; }
    /// <summary>Where files sent by a controller land; nothing from the other PC is written anywhere else.</summary>
    public string ReceiveFolder { get; init; }=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads","HearthLink");
    public HostServer(IdentityStore store,MainWindow window,IPAddress? bindAddress=null) { identity=store; ui=window; listener=new(bindAddress??IPAddress.Any,Connection.Port); }
    public void Start() { listener.Start(8); Listening=true; _=Accept(); }
    public void Disconnect() => active?.Cancel();
    public void Dispose() { lifetime.Cancel(); active?.Cancel(); listener.Stop(); }
    async Task Accept()
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                var tcp=await listener.AcceptTcpClientAsync(lifetime.Token);
                if(!clients.Wait(0)) { tcp.Dispose(); continue; }
                _=Task.Run(async()=>
                {
                    try
                    {
                        // Attempts are counted per source address; a fresh TLS certificate costs an attacker nothing, an address does.
                        string peer; try { var remote=(tcp.Client.RemoteEndPoint as IPEndPoint)?.Address; peer=(remote is {IsIPv4MappedToIPv6:true}?remote.MapToIPv4():remote)?.ToString()??""; } catch(Exception e) when(e is SocketException or ObjectDisposedException) { peer=""; }
                        tcp.NoDelay=true;
                        await Serve(new NetworkStream(tcp.Client,true),peer);
                    }
                    catch(Exception e) when(e is SocketException or ObjectDisposedException or InvalidOperationException or IOException) { }
                    finally { tcp.Dispose(); clients.Release(); }
                });
            }
        }
        catch(OperationCanceledException) { }
        catch(ObjectDisposedException) { }
        catch(SocketException) when(lifetime.IsCancellationRequested) { }
    }
    public async Task ServeRelay(Stream transport,string fromFingerprint)
    {
        if(!clients.Wait(0)) { transport.Dispose(); return; }
        try { await Serve(transport,"relay:"+fromFingerprint); } finally { clients.Release(); }
    }
    public async Task Serve(Stream transport,string peer)
    {
        using(transport)
        using(var ssl=new SslStream(transport,false,(_,cert,_,_)=>cert!=null))
        using(var initial=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            initial.CancelAfter(TimeSpan.FromMinutes(2));
            string fingerprint="";
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate=identity.Certificate,ClientCertificateRequired=true,
                    EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,CertificateRevocationCheckMode=X509RevocationMode.NoCheck
                },initial.Token);
                if(ssl.RemoteCertificate==null) return;
                fingerprint=IdentityStore.FingerprintOf(ssl.RemoteCertificate);
                using var connection=new Connection(transport,ssl);
                // Allow time for the human fingerprint/code comparison after TLS.
                initial.CancelAfter(TimeSpan.FromMinutes(2));
                var request=await connection.Read(initial.Token);
                if(request.Kind=="pair")
                {
                    bool accepted=await ui.OnUi(()=>ui.PairController(fingerprint,request.Name??"",request.Code??"",peer));
                    await connection.Send(new(accepted?"paired":"denied",Name:identity.Config.Name),initial.Token);
                    return;
                }
                if(!identity.Config.Controllers.Contains(fingerprint))
                {
                    await connection.Send(new("denied",Text:"Controller is not trusted. Pair at the host first."),initial.Token);
                    await ui.OnUi(()=> { identity.Audit(fingerprint,"untrusted-rejected"); return true; }); return;
                }
                if(request.Kind is "probe" or "session") await ui.OnUi(()=>ui.UpgradeTrust(fingerprint,peer));
                if(request.Kind=="files")
                {
                    // Files ride a second connection so a large copy never queues behind the video, and only while the control session that granted them lasts.
                    var grant=files;
                    if(grant==null || !grant.Allows(fingerprint,request.Code??"")) { await connection.Send(new("denied",Text:"File transfer needs an active control session."),initial.Token); return; }
                    initial.CancelAfter(Timeout.InfiniteTimeSpan);
                    await new FileHost(ReceiveFolder).Serve(connection,grant.Lifetime); return;
                }
                if(request.Kind=="probe") { await connection.Send(new("online",Name:identity.Config.Name),initial.Token); return; }
                if(request.Kind!="session") return;
                if(request.Value!=Wire.ProtocolVersion) {await connection.Send(new("denied",Text:"Update HearthLink on BOTH PCs to version 0.3.0 or later. Existing pairing is preserved."),initial.Token); return;}
                if(!session.Wait(0)) { await connection.Send(new("denied",Text:"Host already has an active session."),initial.Token); return; }
                try
                {
                    initial.CancelAfter(Timeout.InfiniteTimeSpan);
                    // Access mode leads, before any peer-supplied text.
                    string mode=request.Control?"VIEW AND CONTROL":"VIEW ONLY";
                    bool approved=await ui.OnUi(()=>MessageBox.Show(ui,
                        $"{mode} access requested.\n\nController name: {PeerName.Clean(identity.Config.Controllers.Entries[fingerprint])}\nFingerprint: {fingerprint}\n\nAllow? All visible desktop contents will be shared.",
                        $"HearthLink • {mode} request",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes);
                    if(!approved) { await connection.Send(new("denied",Text:"The person at the host declined."),lifetime.Token); return; }
                    using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    int monitor=0,profile=0;
                    using var window=new FrameWindow();
                    var pressed=new HashSet<int>(); var buttons=new HashSet<int>();
                    Task frames=Task.CompletedTask;
                    // Session state is set only inside this try, so its finally always cancels the grant and clears it, even if "ready" cannot be sent.
                    try
                    {
                        active=stop;
                        var grant=new SessionFileGrant(fingerprint,request.Control,stop.Token);
                        files=request.Control?grant:null;
                        await ui.OnUi(()=> { ui.SessionStarted(identity.Config.Controllers.Entries[fingerprint],request.Control,Disconnect); identity.Audit(fingerprint,"connected"); return true; });
                        await connection.Send(new("ready",Value:Wire.ProtocolVersion,Code:request.Control?grant.Token:null,Monitors:Screen.AllScreens.Select((s,i)=>$"{i+1}: {s.Bounds.Width} × {s.Bounds.Height}").ToArray()),stop.Token);
                        frames=Task.Run(async()=>
                        {
                            try
                            {
                                using var capture=new DesktopCapture();
                                while(!stop.IsCancellationRequested)
                                {
                                    long sequence=await window.AcquireAsync(stop.Token);
                                    if(!identity.Config.Controllers.Contains(fingerprint)) break;
                                    long cycle=Stopwatch.GetTimestamp(); int mode=Volatile.Read(ref profile);
                                    var frame=capture.Capture(Volatile.Read(ref monitor),sequence,mode,window.RoundTripMilliseconds);
                                    stop.Token.ThrowIfCancellationRequested();
                                    if(frame==null) window.Abandon(sequence);
                                    else { window.MarkSent(sequence); await connection.Send(new("frame",Frame:frame),stop.Token); }
                                    double remaining=1000d/DesktopCapture.Profile(mode).Fps-Stopwatch.GetElapsedTime(cycle).TotalMilliseconds;
                                    if(remaining>1) await Task.Delay(TimeSpan.FromMilliseconds(remaining),stop.Token);
                                }
                            }
                            finally { stop.Cancel(); }
                        });
                        while(!stop.IsCancellationRequested)
                        {
                            var p=await connection.Read(stop.Token);
                            if(!identity.Config.Controllers.Contains(fingerprint)) break;
                            if(p.Kind=="disconnect") break;
                            if(p.Kind=="presented") {window.Acknowledge(p.Sequence); continue;}
                            if(p.Kind=="monitor" && p.Value>=0 && p.Value<Screen.AllScreens.Length) { Volatile.Write(ref monitor,p.Value); continue; }
                            if(p.Kind=="profile" && p.Value>=0 && p.Value<DesktopCapture.Profiles.Length) {Volatile.Write(ref profile,p.Value); continue;}
                            if(!request.Control) continue;
                            if(p.Kind=="release")
                            {
                                foreach(int key in pressed) Desktop.Input(new("key",Value:key),monitor);
                                foreach(int button in buttons) Desktop.Input(new("button",Value:button),monitor);
                                pressed.Clear(); buttons.Clear(); continue;
                            }
                            if(p.Kind=="key") { if(p.Down) pressed.Add(p.Value); else pressed.Remove(p.Value); }
                            if(p.Kind=="button") { if(p.Down) buttons.Add(p.Value); else buttons.Remove(p.Value); }
                            Desktop.Input(p,Volatile.Read(ref monitor));
                        }
                    }
                    finally
                    {
                        stop.Cancel();
                        try { await frames; } catch(Exception e) when(e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
                        foreach(int key in pressed) { try { Desktop.Input(new("key",Value:key),monitor); } catch { } }
                        foreach(int button in buttons) { try { Desktop.Input(new("button",Value:button),monitor); } catch { } }
                        active=null; files=null;
                        await ui.OnUi(()=> { ui.SessionEnded(); identity.Audit(fingerprint,"disconnected"); return true; });
                    }
                }
                finally { session.Release(); }
            }
            catch(Exception e) when(e is IOException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or System.Net.WebSockets.WebSocketException or TimeoutException)
            {
                // Do not log wire payloads or exception text containing user data.
            }
        }
    }
}
