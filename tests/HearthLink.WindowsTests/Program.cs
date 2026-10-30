using HearthLink;
using HearthLink.Core;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

static async Task<(RelayStream Client,RelayStream Server)> RelayPair()
{
    var tcpListener=new TcpListener(IPAddress.Loopback,0); tcpListener.Start();
    try
    {
        var client=new TcpClient(); var accepting=tcpListener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback,((IPEndPoint)tcpListener.LocalEndpoint).Port);
        var server=await accepting;
        return (new RelayStream(WebSocket.CreateFromStream(client.GetStream(),false,null,TimeSpan.FromSeconds(30))),new RelayStream(WebSocket.CreateFromStream(server.GetStream(),true,null,TimeSpan.FromSeconds(30))));
    }
    finally { tcpListener.Stop(); }
}
/// <summary>A loopback WebSocket server that accepts one upgrade; returns its ws:// address and whether a client completed the handshake.</summary>
static (Uri Address,Task<bool> Accepted) WebSocketServer()
{
    var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
    var address=new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/status");
    return (address,Task.Run(async()=>
    {
        try
        {
            using var client=await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var stream=client.GetStream(); var request=new System.Text.StringBuilder(); var one=new byte[1];
            while(!request.ToString().EndsWith("\r\n\r\n") && await stream.ReadAsync(one)==1) request.Append((char)one[0]);
            string key=request.ToString().Split("\r\n").First(l=>l.StartsWith("Sec-WebSocket-Key:",StringComparison.OrdinalIgnoreCase))[18..].Trim();
            string accept=Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
            return true;
        }
        catch(Exception) { return false; }
        finally { listener.Stop(); }
    }));
}
/// <summary>A "proxy" that accepts connections and never answers, like a dead free public proxy. Counts connections it received.</summary>
static (WebProxy Proxy,Func<int> Connections,Action Stop) StalledProxy()
{
    var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); int connections=0; var held=new List<TcpClient>();
    _=Task.Run(async()=> { try { while(true) { var c=await listener.AcceptTcpClientAsync(); lock(held) held.Add(c); Interlocked.Increment(ref connections); } } catch(Exception) { } });
    return (new WebProxy($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"),()=>Volatile.Read(ref connections),()=> { listener.Stop(); lock(held) held.ForEach(c=>c.Dispose()); });
}
{ var free=new TcpListener(IPAddress.Loopback,0); free.Start(); Connection.Port=((IPEndPoint)free.LocalEndpoint).Port; free.Stop(); }
var root=Path.Combine(Path.GetTempPath(),"HearthLink-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed=0;
void Check(bool value,string name) { if(!value) throw new Exception("FAIL: "+name); Console.WriteLine("PASS: "+name); passed++; }
try
{
    {
        // A system proxy set by a free "VPN" (Windows user proxy) must not stall the relay: direct goes first.
        var (proxy,proxyConnections,stopProxy)=StalledProxy();
        try
        {
            var (viaProxy,_)=WebSocketServer();
            using(var routed=new ClientWebSocket()) { routed.Options.Proxy=proxy; bool stalled=false; try { await routed.ConnectAsync(viaProxy,new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token); } catch(OperationCanceledException) { stalled=true; } Check(stalled,"fixture: a WebSocket routed through the stalled proxy hangs"); }
            var (direct,accepted)=WebSocketServer(); var clock=System.Diagnostics.Stopwatch.StartNew();
            using(var socket=await RelayClient.Dial(direct,proxy,new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
                Check(socket.State==WebSocketState.Open && await accepted && clock.Elapsed<TimeSpan.FromSeconds(3),"relay dials direct when a stalled system proxy is set");
            int before=proxyConnections(); bool failed=false;
            var closed=new TcpListener(IPAddress.Loopback,0); closed.Start(); var unreachable=new Uri($"ws://127.0.0.1:{((IPEndPoint)closed.LocalEndpoint).Port}/v1/status"); closed.Stop();
            try { using var _=await RelayClient.Dial(unreachable,proxy,new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token); } catch(Exception) { failed=true; }
            Check(failed && proxyConnections()>before,"relay falls back to the system proxy when direct fails");
        }
        finally { stopProxy(); }
    }
    var host=new IdentityStore(Path.Combine(root,"host"));
    var controller=new IdentityStore(Path.Combine(root,"controller"));
    var stranger=new IdentityStore(Path.Combine(root,"stranger"));
    Check(host.Fingerprint!=controller.Fingerprint,"unique device identities");
    Check(host.Fingerprint==new IdentityStore(Path.Combine(root,"host")).Fingerprint,"DPAPI identity survives restart");
    host.Config.Controllers.Add(controller.Fingerprint,"controller"); host.Save();
    Check(new IdentityStore(Path.Combine(root,"host")).Config.Controllers.Contains(controller.Fingerprint),"protected trust persistence");
    host.Config.LastTarget="TECHNODESK"; host.Save();
    Check(new IdentityStore(Path.Combine(root,"host")).Config.LastTarget=="TECHNODESK","computer name in pair box survives restart");
    {
        // A saved profile must follow the app's built-in relay; only an address the user chose is kept.
        var relayProfile=new IdentityStore(Path.Combine(root,"relay-profile")); relayProfile.Save();
        bool followsDefault=new IdentityStore(Path.Combine(root,"relay-profile")).RelayUrl==RelayClient.DefaultUrl;
        relayProfile.Config.RelayUrl="wss://hearthlink-relay.REPLACE.workers.dev"; relayProfile.Save();
        bool placeholderIgnored=new IdentityStore(Path.Combine(root,"relay-profile")).RelayUrl==RelayClient.DefaultUrl;
        relayProfile.Config.RelayUrl="wss://relay.example.com"; relayProfile.Save();
        bool chosenKept=new IdentityStore(Path.Combine(root,"relay-profile")).RelayUrl=="wss://relay.example.com";
        Check(followsDefault && placeholderIgnored && chosenKept,"saved settings follow the built-in relay address");
    }
    byte[] protectedKey=File.ReadAllBytes(Path.Combine(root,"host","identity.protected"));
    Check(protectedKey[0]!=0x30,"private key is not stored as raw PFX");
    var listener=new TcpListener(IPAddress.Loopback,Connection.Port); listener.Start();
    try
    {
        async Task<string> Serve(bool expectRejection=false)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var tcp=await listener.AcceptTcpClientAsync(timeout.Token);
            using var ssl=new SslStream(tcp.GetStream(),false,(_,cert,_,_)=>cert!=null);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {ServerCertificate=host.Certificate,ClientCertificateRequired=true,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13},timeout.Token);
                string peer=IdentityStore.FingerprintOf(ssl.RemoteCertificate!);
                var packet=await Wire.Read(ssl,timeout.Token);
                bool trusted=host.Config.Controllers.Contains(peer);
                await Wire.Write(ssl,new(trusted?"online":"denied"),timeout.Token);
                return peer;
            }
            catch(Exception e) when(e is IOException or AuthenticationException or OperationCanceledException) { if(!expectRejection) Console.WriteLine("Fixture TLS diagnostic: "+e); return "handshake-rejected"; }
        }
        var serving=Serve();
        using(var c=await Connection.Connect("127.0.0.1",controller,host.Fingerprint))
        {
            Check(c.Stream.IsEncrypted && c.Stream.IsMutuallyAuthenticated,"actual encrypted mutual TLS loopback connection");
            await c.Send(new("probe")); Check((await c.Read()).Kind=="online","trusted identity authenticated over TLS");
        }
        Check(await serving==controller.Fingerprint,"server sees controller certificate identity");
        serving=Serve();
        using(var c=await Connection.Connect("127.0.0.1",stranger,host.Fingerprint)) { await c.Send(new("probe")); Check((await c.Read()).Kind=="denied","untrusted identity rejected after TLS"); }
        await serving;
        host.Config.Controllers.Remove(controller.Fingerprint);
        serving=Serve();
        using(var c=await Connection.Connect("127.0.0.1",controller,host.Fingerprint)) { await c.Send(new("probe")); Check((await c.Read()).Kind=="denied","revoked identity rejected over TLS"); }
        await serving;
        serving=Serve(true); bool rejected=false;
        try { using var c=await Connection.Connect("127.0.0.1",controller,stranger.Fingerprint); } catch(AuthenticationException) { rejected=true; }
        Check(rejected,"changed host identity fails TLS handshake"); await serving;
    }
    finally { listener.Stop(); }
    // Exercise the actual application host, with its real UI dispatcher and isolated fixture identity.
    host.Config.Controllers.Add(controller.Fingerprint,"controller"); host.Save();
    var ready=new TaskCompletionSource<(MainWindow Window,HostServer Server)>(TaskCreationOptions.RunContinuationsAsynchronously);
    var uiThread=new Thread(()=>
    {
        try
        {
            using var window=new MainWindow(host) {ShowInTaskbar=false,Opacity=0,WindowState=FormWindowState.Minimized};
            HostServer? actual=null;
            window.Shown+=(_,_)=> { actual=new HostServer(host,window,IPAddress.Loopback) {ReceiveFolder=Path.Combine(root,"host-receive")}; actual.Start(); ready.SetResult((window,actual)); };
            Application.Run(window); actual?.Dispose();
        }
        catch(Exception e) { ready.TrySetException(e); }
    });
    uiThread.SetApartmentState(ApartmentState.STA); uiThread.Start();
    var fixture=await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    try
    {
        await DeviceListChecks.Run(fixture.Window,Check);
        await PendingTrustChecks.Run(fixture.Window,host,controller,Check);
        await NavigationChecks.Run(fixture.Window,Check);
        if(args.Contains("--navigation-only")) {Console.WriteLine($"{passed} focused Windows checks passed."); return;}
        await UpdateUiChecks.Run(fixture.Window,host,Check);
        Check(DesktopCapture.Profile(2)==(8192,8192,75,30) && DesktopCapture.Profile(3)==(8192,8192,85,30) && DesktopCapture.Profile(4)==(8192,8192,95,30) && DesktopCapture.Profiles.SequenceEqual(["Responsive","Balanced","Full detail","Sharp","Best"]),"Sharp and Best profiles send full resolution at quality 85 and 95");
        await ViewerChecks.Run(fixture.Window,host,controller,Check);
        await FileChecks.RunStrip(fixture.Window,host,controller,root,Check);
        await FileChecks.Run(root,Check);
        await FileChecks.RunReconnect(host,controller,root,Check);
        await FileChecks.RunLimits(host,controller,root,Check);
        await FileChecks.RunSessions(fixture.Window,host,controller,Path.Combine(root,"host-receive"),root,Check);
        await UnattendedChecks.Run(fixture.Window,fixture.Server,host,controller,stranger,root,Check);
        using(var c=await Connection.Connect("127.0.0.1",controller,host.Fingerprint))
        {
            await c.Send(new("probe")); Check((await c.Read()).Kind=="online","actual HostServer accepts trusted status probe");
        }
        {
            var (clientTransport,serverTransport)=await RelayPair();
            var relayed=fixture.Server.ServeRelay(serverTransport,controller.Fingerprint);
            using var c=await Connection.Secure(clientTransport,controller,host.Fingerprint,null,CancellationToken.None);
            await c.Send(new("probe")); Check((await c.Read()).Kind=="online","HostServer serves a session over an in-memory relay stream");
            c.Dispose(); await relayed.WaitAsync(TimeSpan.FromSeconds(10));
        }
        {
            var (dropClient,dropServer)=await RelayPair();
            var dropped=fixture.Server.ServeRelay(dropServer,controller.Fingerprint);
            dropClient.Dispose();
            bool completed=true; try { await dropped.WaitAsync(TimeSpan.FromSeconds(10)); } catch { completed=false; }
            var (aliveClient,aliveServer)=await RelayPair();
            var again=fixture.Server.ServeRelay(aliveServer,controller.Fingerprint);
            using var c=await Connection.Secure(aliveClient,controller,host.Fingerprint,null,CancellationToken.None);
            await c.Send(new("probe")); Check(completed && (await c.Read()).Kind=="online","ServeRelay survives a peer dropping mid-handshake and releases its slot");
            c.Dispose(); await again.WaitAsync(TimeSpan.FromSeconds(10));
        }
        using(var c=await Connection.Connect("127.0.0.1",controller,host.Fingerprint))
        {
            await c.Send(new("session",Control:true,Value:Wire.ProtocolVersion-1));
            var reply=await c.Read(); Check(reply.Kind=="denied" && (reply.Text??"").Contains("Update HearthLink on BOTH PCs"),"an older protocol session gets update message");
        }
        using(var c=await Connection.Connect("127.0.0.1",stranger,host.Fingerprint))
        {
            await c.Send(new("session",Control:true)); Check((await c.Read()).Kind=="denied","actual HostServer denies untrusted control request before consent/capture");
        }
        using(var c=await Connection.Connect("127.0.0.1",stranger,host.Fingerprint))
        {
            await c.Send(new("pair",Name:"test stranger",Code:"00000000")); Check((await c.Read()).Kind=="denied","actual HostServer denies pairing without an open window");
        }
        bool HasHost(IdentityStore store,string fingerprint,string address)=>store.Config.Hosts.Exists(p=>p.Fingerprint==fingerprint && p.Address==address);
        using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        uint dialogThread=await fixture.Window.OnUi(NativeDialogs.CurrentThread);
        var newcomer=new IdentityStore(Path.Combine(root,"newcomer")); newcomer.Config.Name="newcomer";
        {
            string code=await fixture.Window.OnUi(fixture.Window.OpenPairing);
            using var c=await Connection.Connect("127.0.0.1",newcomer,null,actual=>actual==host.Fingerprint);
            var pairing=c.Pair(newcomer,code,"fallback","127.0.0.1",limit.Token);
            string prompt=await NativeDialogs.Answer(dialogThread,NativeDialogs.Yes,TimeSpan.FromSeconds(15));
            string? name=await pairing;
            bool hostSide=await fixture.Window.OnUi(()=>host.Config.Controllers.Contains(newcomer.Fingerprint) && HasHost(host,newcomer.Fingerprint,"127.0.0.1"));
            Check(name!=null && prompt.StartsWith("Trust this PC both ways?") && hostSide && newcomer.Config.Controllers.Contains(host.Fingerprint) && HasHost(newcomer,host.Fingerprint,"127.0.0.1"),"pairing stores both directions");
        }
        var oneWay=new IdentityStore(Path.Combine(root,"one-way"));
        oneWay.Config.Hosts.Add(new("host","127.0.0.1",host.Fingerprint)); oneWay.Save();
        await fixture.Window.OnUi(()=> { host.Config.Controllers.Add(oneWay.Fingerprint,"one-way"); host.Save(); return true; });
        using(var c=await Connection.Connect("127.0.0.1",oneWay,host.Fingerprint))
        {
            bool online=(await c.Probe(oneWay,limit.Token)).Kind=="online";
            bool hostSide=await fixture.Window.OnUi(()=>HasHost(host,oneWay.Fingerprint,"127.0.0.1"));
            Check(online && hostSide && oneWay.Config.Controllers.Contains(host.Fingerprint) && new IdentityStore(Path.Combine(root,"one-way")).Config.Controllers.Contains(host.Fingerprint),"one-way pairing upgrades on next probe");
        }
        // The stranger holds the host in its own list, but the host never paired it: neither side may gain trust.
        stranger.Config.Hosts.Add(new("host","127.0.0.1",host.Fingerprint));
        using(var c=await Connection.Connect("127.0.0.1",stranger,host.Fingerprint))
        {
            bool denied=(await c.Probe(stranger,limit.Token)).Kind=="denied";
            bool hostSide=await fixture.Window.OnUi(()=>host.Config.Controllers.Contains(stranger.Fingerprint) || host.Config.Hosts.Exists(p=>p.Fingerprint==stranger.Fingerprint));
            Check(denied && !hostSide && !stranger.Config.Controllers.Contains(host.Fingerprint),"upgrade never trusts an unpaired PC");
        }
        stranger.Config.Hosts.Clear();
        {
            bool removed=await fixture.Window.OnUi(()=>host.Remove(oneWay.Fingerprint));
            var reloaded=new IdentityStore(Path.Combine(root,"host"));
            bool gone(IdentityStore store)=>!store.Config.Controllers.Contains(oneWay.Fingerprint) && !store.Config.Hosts.Exists(p=>p.Fingerprint==oneWay.Fingerprint);
            Check(removed && await fixture.Window.OnUi(()=>gone(host)) && gone(reloaded),"remove PC clears both roles");
        }
        string? relayUrl=Environment.GetEnvironmentVariable("HEARTHLINK_RELAY_URL");
        if(string.IsNullOrWhiteSpace(relayUrl)) Console.WriteLine("SKIP: relay checks (HEARTHLINK_RELAY_URL not set)");
        else await RelayChecks.Run(relayUrl.Trim(),fixture.Window,fixture.Server,host,controller,stranger,root,Check);
        await fixture.Window.OnUi(()=> { host.Config.Controllers.Remove(controller.Fingerprint); host.Save(); return true; });
        using(var c=await Connection.Connect("127.0.0.1",controller,host.Fingerprint))
        {
            await c.Send(new("session",Control:true)); Check((await c.Read()).Kind=="denied","actual HostServer denies revoked controller");
        }
    }
    finally
    {
        await fixture.Window.OnUi(()=> { fixture.Server.Dispose(); fixture.Window.Close(); return true; });
        if(!uiThread.Join(TimeSpan.FromSeconds(10))) throw new Exception("UI fixture did not exit");
    }
    Console.WriteLine($"{passed} Windows checks passed. No two-PC connection tested.");
}
finally
{
    // Only this freshly created fixture folder is removed, never a computed user data directory.
    Directory.Delete(root,true);
}
