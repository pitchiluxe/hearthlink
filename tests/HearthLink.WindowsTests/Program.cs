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
var root=Path.Combine(Path.GetTempPath(),"HearthLink-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed=0;
void Check(bool value,string name) { if(!value) throw new Exception("FAIL: "+name); Console.WriteLine("PASS: "+name); passed++; }
try
{
    var host=new IdentityStore(Path.Combine(root,"host"));
    var controller=new IdentityStore(Path.Combine(root,"controller"));
    var stranger=new IdentityStore(Path.Combine(root,"stranger"));
    Check(host.Fingerprint!=controller.Fingerprint,"unique device identities");
    Check(host.Fingerprint==new IdentityStore(Path.Combine(root,"host")).Fingerprint,"DPAPI identity survives restart");
    host.Config.Controllers.Add(controller.Fingerprint,"controller"); host.Save();
    Check(new IdentityStore(Path.Combine(root,"host")).Config.Controllers.Contains(controller.Fingerprint),"protected trust persistence");
    host.Config.LastTarget="TECHNODESK"; host.Save();
    Check(new IdentityStore(Path.Combine(root,"host")).Config.LastTarget=="TECHNODESK","computer name in pair box survives restart");
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
            window.Shown+=(_,_)=> { actual=new HostServer(host,window,IPAddress.Loopback); actual.Start(); ready.SetResult((window,actual)); };
            Application.Run(window); actual?.Dispose();
        }
        catch(Exception e) { ready.TrySetException(e); }
    });
    uiThread.SetApartmentState(ApartmentState.STA); uiThread.Start();
    var fixture=await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    try
    {
        await UpdateUiChecks.Run(fixture.Window,host,Check);
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
            await c.Send(new("session",Control:true,Value:2));
            var reply=await c.Read(); Check(reply.Kind=="denied" && (reply.Text??"").Contains("0.3.0"),"protocol 2 session gets update message");
        }
        using(var c=await Connection.Connect("127.0.0.1",stranger,host.Fingerprint))
        {
            await c.Send(new("session",Control:true)); Check((await c.Read()).Kind=="denied","actual HostServer denies untrusted control request before consent/capture");
        }
        using(var c=await Connection.Connect("127.0.0.1",stranger,host.Fingerprint))
        {
            await c.Send(new("pair",Name:"test stranger",Code:"00000000")); Check((await c.Read()).Kind=="denied","actual HostServer denies pairing without an open window");
        }
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
