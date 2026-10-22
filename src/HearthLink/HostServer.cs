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
    public bool HasActiveSession=>active!=null;
    public HostServer(IdentityStore store,MainWindow window,IPAddress? bindAddress=null) { identity=store; ui=window; listener=new(bindAddress??IPAddress.Any,Connection.Port); }
    public void Start() { listener.Start(8); _=Accept(); }
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
                _=Task.Run(async()=> { try { await Serve(tcp); } finally { clients.Release(); } });
            }
        }
        catch(OperationCanceledException) { }
        catch(ObjectDisposedException) { }
        catch(SocketException) when(lifetime.IsCancellationRequested) { }
    }
    async Task Serve(TcpClient tcp)
    {
        using(tcp)
        using(var ssl=new SslStream(tcp.GetStream(),false,(_,cert,_,_)=>cert!=null))
        using(var initial=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            initial.CancelAfter(TimeSpan.FromMinutes(2));
            string fingerprint="";
            try
            {
                tcp.NoDelay=true;
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate=identity.Certificate,ClientCertificateRequired=true,
                    EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,CertificateRevocationCheckMode=X509RevocationMode.NoCheck
                },initial.Token);
                if(ssl.RemoteCertificate==null) return;
                fingerprint=IdentityStore.FingerprintOf(ssl.RemoteCertificate);
                using var connection=new Connection(tcp,ssl);
                // Allow time for the human fingerprint/code comparison after TLS.
                initial.CancelAfter(TimeSpan.FromMinutes(2));
                var request=await connection.Read(initial.Token);
                if(request.Kind=="pair")
                {
                    bool accepted=await ui.OnUi(()=>ui.PairController(fingerprint,request.Name??"Controller",request.Code??""));
                    await connection.Send(new(accepted?"paired":"denied",Name:identity.Config.Name),initial.Token);
                    return;
                }
                if(!identity.Config.Controllers.Contains(fingerprint))
                {
                    await connection.Send(new("denied",Text:"Controller is not trusted. Pair at the host first."),initial.Token);
                    await ui.OnUi(()=> { identity.Audit(fingerprint,"untrusted-rejected"); return true; }); return;
                }
                if(request.Kind=="probe") { await connection.Send(new("online",Name:identity.Config.Name),initial.Token); return; }
                if(request.Kind!="session") return;
                if(request.Value!=Wire.ProtocolVersion) {await connection.Send(new("denied",Text:"Update HearthLink on BOTH PCs to version 0.2.0 or later for faster streaming. Existing pairing is preserved."),initial.Token); return;}
                if(!session.Wait(0)) { await connection.Send(new("denied",Text:"Host already has an active session."),initial.Token); return; }
                try
                {
                    initial.CancelAfter(Timeout.InfiniteTimeSpan);
                    bool approved=await ui.OnUi(()=>MessageBox.Show(ui,
                        $"Allow {identity.Config.Controllers.Entries[fingerprint]} to {(request.Control?"VIEW AND CONTROL":"VIEW")} your desktop?\n\nController: {fingerprint}\n\nAll visible desktop contents will be shared.",
                        "HearthLink • incoming session",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes);
                    if(!approved) { await connection.Send(new("denied",Text:"The person at the host declined."),lifetime.Token); return; }
                    using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    active=stop;
                    await ui.OnUi(()=> { ui.SessionStarted(identity.Config.Controllers.Entries[fingerprint],request.Control,Disconnect); identity.Audit(fingerprint,"connected"); return true; });
                    await connection.Send(new("ready",Value:Wire.ProtocolVersion,Monitors:Screen.AllScreens.Select((s,i)=>$"{i+1}: {s.Bounds.Width} × {s.Bounds.Height}").ToArray()),stop.Token);
                    int monitor=0,profile=0;
                    using var window=new FrameWindow();
                    var pressed=new HashSet<int>(); var buttons=new HashSet<int>();
                    Task frames=Task.Run(async()=>
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
                                stop.Token.ThrowIfCancellationRequested(); window.MarkSent(sequence);
                                await connection.Send(new("frame",Frame:frame),stop.Token);
                                double remaining=1000d/DesktopCapture.Profile(mode).Fps-Stopwatch.GetElapsedTime(cycle).TotalMilliseconds;
                                if(remaining>1) await Task.Delay(TimeSpan.FromMilliseconds(remaining),stop.Token);
                            }
                        }
                        finally { stop.Cancel(); }
                    });
                    try
                    {
                        while(!stop.IsCancellationRequested)
                        {
                            var p=await connection.Read(stop.Token);
                            if(!identity.Config.Controllers.Contains(fingerprint)) break;
                            if(p.Kind=="disconnect") break;
                            if(p.Kind=="presented") {window.Acknowledge(p.Sequence); continue;}
                            if(p.Kind=="monitor" && p.Value>=0 && p.Value<Screen.AllScreens.Length) { Volatile.Write(ref monitor,p.Value); continue; }
                            if(p.Kind=="profile" && p.Value>=0 && p.Value<=2) {Volatile.Write(ref profile,p.Value); continue;}
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
                        active=null;
                        await ui.OnUi(()=> { ui.SessionEnded(); identity.Audit(fingerprint,"disconnected"); return true; });
                    }
                }
                finally { session.Release(); }
            }
            catch(Exception e) when(e is IOException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or TimeoutException)
            {
                // Do not log wire payloads or exception text containing user data.
            }
        }
    }
}
