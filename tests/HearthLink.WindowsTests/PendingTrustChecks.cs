using HearthLink;
using HearthLink.Core;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;

static class PendingTrustChecks
{
    public static async Task Run(MainWindow owner,IdentityStore identity,IdentityStore remote,Action<bool,string> check)
    {
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        int oldPort=Connection.Port; string? oldRelay=identity.RelayOverride;
        var requested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approve=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serving=Task.Run(async()=>
        {
            using var tcp=await listener.AcceptTcpClientAsync(timeout.Token);
            using var ssl=new SslStream(tcp.GetStream(),false,(_,certificate,_,_)=>certificate!=null);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {ServerCertificate=remote.Certificate,ClientCertificateRequired=true,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13},timeout.Token);
            await Wire.Read(ssl,timeout.Token); requested.TrySetResult();
            await approve.Task.WaitAsync(timeout.Token);
            await Wire.Write(ssl,new("ready",Value:Wire.ProtocolVersion,Monitors:["Fixture display"]),timeout.Token);
        });
        try
        {
            var connecting=await owner.OnUi(()=>
            {
                Connection.Port=((IPEndPoint)listener.LocalEndpoint).Port; identity.RelayOverride="invalid";
                identity.Config.Hosts.Add(new("Pending approval fixture","127.0.0.1",remote.Fingerprint));
                return (Task)typeof(MainWindow).GetMethod("ConnectTo",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owner,[remote.Fingerprint,false])!;
            });
            await requested.Task.WaitAsync(timeout.Token);
            await owner.OnUi(()=> {identity.Config.Hosts.RemoveAll(p=>p.Fingerprint==remote.Fingerprint); return true;});
            approve.TrySetResult(); await serving;
            bool rejected=false;
            try {await connecting.WaitAsync(timeout.Token);}
            catch(InvalidOperationException e) when(e.Message.Contains("paired")) {rejected=true;}
            bool noViewer=await owner.OnUi(()=>!owner.OwnedForms.OfType<Viewer>().Any(v=>v.SessionActive));
            check(rejected && noViewer,"removing a paired PC during approval prevents the pending viewer from opening");
        }
        finally
        {
            timeout.Cancel(); approve.TrySetResult(); listener.Stop();
            await owner.OnUi(()=>
            {
                Connection.Port=oldPort; identity.RelayOverride=oldRelay;
                identity.Config.Hosts.RemoveAll(p=>p.Fingerprint==remote.Fingerprint);
                foreach(var viewer in owner.OwnedForms.OfType<Viewer>().ToArray()) viewer.Close();
                return true;
            });
            try {await serving;} catch(Exception e) when(e is OperationCanceledException or IOException or SocketException) { }
        }
    }
}
