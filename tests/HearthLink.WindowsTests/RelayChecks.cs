using HearthLink;
using HearthLink.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

// Integration checks against a running relay (`cd relay && npx wrangler dev --port 8787`, HEARTHLINK_RELAY_URL=ws://127.0.0.1:8787).
static class RelayChecks
{
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread,EnumWindows callback,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,System.Text.StringBuilder name,int size);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    delegate bool EnumWindows(IntPtr window,IntPtr state);
    const string Online="Online through relay";

    static async Task Until(Func<bool> condition,TimeSpan limit)
    {
        var clock=Stopwatch.StartNew();
        while(!condition()) { if(clock.Elapsed>limit) throw new TimeoutException("Condition not reached within "+limit); await Task.Delay(100); }
    }
    static async Task<bool> Reached(Func<bool> condition,TimeSpan limit) { try { await Until(condition,limit); return true; } catch(TimeoutException) { return false; } }
    /// <summary>Message boxes (#32770) currently open on the given UI thread.</summary>
    static List<IntPtr> Dialogs(uint thread)
    {
        var found=new List<IntPtr>();
        EnumThreadWindows(thread,(window,_)=> { var name=new System.Text.StringBuilder(64); GetClassName(window,name,name.Capacity); if(name.ToString()=="#32770") found.Add(window); return true; },IntPtr.Zero);
        return found;
    }
    public static async Task Run(string relayUrl,MainWindow window,HostServer server,IdentityStore host,IdentityStore controller,IdentityStore stranger,string root,Action<bool,string> check)
    {
        using var lifetime=new CancellationTokenSource(TimeSpan.FromMinutes(5)); var token=lifetime.Token;
        var second=new IdentityStore(Path.Combine(root,"second-controller"));
        await window.OnUi(()=> { host.Config.Controllers.Add(second.Fingerprint,"second"); return true; });
        host.Config.RelayUrl=relayUrl;
        using var presence=new RelayPresence(host,()=>[controller.Fingerprint,second.Fingerprint],(stream,from)=>server.ServeRelay(stream,from));
        presence.Start();
        await Until(()=>presence.State==Online,TimeSpan.FromSeconds(15));
        check((await RelayClient.Online(relayUrl,controller,[host.Fingerprint],token)).Contains(host.Fingerprint),"relay presence shows host online to allowed controller");
        check((await RelayClient.Online(relayUrl,stranger,[host.Fingerprint],token)).Count==0,"relay hides host from stranger");

        using(var c=await RelayClient.ConnectHost(relayUrl,controller,host.Fingerprint,token))
        {
            await c.Send(new("probe"),token);
            check((await c.Read(token)).Kind=="online" && c.Stream.IsEncrypted && c.Stream.IsMutuallyAuthenticated,"relayed probe uses pinned TLS end to end");
        }

        // A relay that routes to the wrong PC: the decoy's presence is genuine, but the tunnel ends at the real host's TLS identity.
        var decoy=new IdentityStore(Path.Combine(root,"decoy")); decoy.Config.RelayUrl=relayUrl;
        using(var misrouted=new RelayPresence(decoy,()=>[controller.Fingerprint],(stream,from)=>server.ServeRelay(stream,from)))
        {
            misrouted.Start(); await Until(()=>misrouted.State==Online,TimeSpan.FromSeconds(15));
            bool rejected=false;
            try { using var c=await RelayClient.ConnectHost(relayUrl,controller,decoy.Fingerprint,token); } catch(AuthenticationException) { rejected=true; }
            check(rejected,"relayed connect to wrong fingerprint fails TLS");
        }

        var pairHost=new IdentityStore(Path.Combine(root,"pair-host")); pairHost.Config.RelayUrl=relayUrl;
        var gate=new PairingGate(); var gateLock=new object();
        using(var pairing=new RelayPresence(pairHost,()=>[],async(stream,from)=>
        {
            using(stream)
            using(var ssl=new SslStream(stream,false,(_,cert,_,_)=>cert!=null))
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {ServerCertificate=pairHost.Certificate,ClientCertificateRequired=true,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,CertificateRevocationCheckMode=X509RevocationMode.NoCheck},token);
                var request=await Wire.Read(ssl,token);
                bool accepted; lock(gateLock) accepted=request.Kind=="pair" && gate.Consume(request.Code??"",DateTimeOffset.UtcNow,"relay:"+from);
                await Wire.Write(ssl,new(accepted?"paired":"denied",Name:"pair-host"),token);
            }
        }))
        {
            pairing.Start(); await Until(()=>pairing.State==Online,TimeSpan.FromSeconds(15));
            PairingCode code=default; bool opened=false;
            for(int attempt=0;attempt<5 && !opened;attempt++) { lock(gateLock) code=PairingCode.New(gate.Open(DateTimeOffset.UtcNow)); opened=await pairing.OpenPairing(code.Lookup); }
            string? confirmed=null;
            using(var c=await RelayClient.ConnectForPairing(relayUrl,controller,code,actual=> { confirmed=actual; return actual==pairHost.Fingerprint; },token))
            {
                await c.Send(new("pair",Name:"controller",Code:code.Secret),token);
                check(opened && confirmed==pairHost.Fingerprint && (await c.Read(token)).Kind=="paired","pairing through relay by code");
            }
            await pairing.ClosePairing();
        }

        // Review focus: the relay pipes a second session, and the host app answers busy.
        uint uiThread=await window.OnUi(GetCurrentThreadId);
        using(var first=await RelayClient.ConnectHost(relayUrl,controller,host.Fingerprint,token))
        {
            await first.Send(new("session",Control:false,Value:Wire.ProtocolVersion),token);
            // The approval prompt appears only after the host has claimed its single session slot.
            await Until(()=>Dialogs(uiThread).Count>0,TimeSpan.FromSeconds(15));
            Packet busy;
            using(var c=await RelayClient.ConnectHost(relayUrl,second,host.Fingerprint,token))
            {
                await c.Send(new("session",Control:true,Value:Wire.ProtocolVersion),token);
                busy=await c.Read(token);
            }
            foreach(var dialog in Dialogs(uiThread)) PostMessage(dialog,0x0111 /* WM_COMMAND */,7 /* IDNO */,IntPtr.Zero);
            var declined=await first.Read(token);
            check(busy.Kind=="denied" && busy.Text=="Host already has an active session." && declined.Kind=="denied","second relayed session gets busy message");
        }
        await window.OnUi(()=> { host.Config.Controllers.Remove(second.Fingerprint); return true; });
        await Restart(root,check,token);
    }

    /// <summary>Runs a private `wrangler dev` (own port and storage) so stopping it never disturbs the relay named by HEARTHLINK_RELAY_URL.</summary>
    static async Task Restart(string root,Action<bool,string> check,CancellationToken token)
    {
        string? relay=AppContext.BaseDirectory;
        while(relay!=null && !File.Exists(Path.Combine(relay,"relay","wrangler.toml"))) relay=Path.GetDirectoryName(relay);
        if(relay==null) throw new Exception("FAIL: relay folder not found above "+AppContext.BaseDirectory);
        relay=Path.Combine(relay,"relay");
        int FreePort() { var l=new TcpListener(IPAddress.Loopback,0); l.Start(); int p=((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
        int port=FreePort(), inspector=FreePort(); string state=Path.Combine(root,"relay-state");
        var output=new Queue<string>();
        void Keep(string? line) { if(line==null) return; lock(output) { output.Enqueue(line); while(output.Count>40) output.Dequeue(); } }
        // Node runs wrangler directly: no cmd/npx argument re-quoting, and stdin is a pipe because an inherited console handle breaks Node worker threads.
        Process Launch()
        {
            var start=new ProcessStartInfo("node") {WorkingDirectory=relay,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var argument in new[] {Path.Combine(relay,"node_modules","wrangler","bin","wrangler.js"),"dev","--port",port.ToString(),"--inspector-port",inspector.ToString(),"--persist-to",state,"--show-interactive-dev-session=false"}) start.ArgumentList.Add(argument);
            start.Environment["WRANGLER_SEND_METRICS"]="false";
            var process=Process.Start(start)!; process.OutputDataReceived+=(_,e)=>Keep(e.Data); process.ErrorDataReceived+=(_,e)=>Keep(e.Data); process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
        }
        async Task Ready()
        {
            using var http=new HttpClient {Timeout=TimeSpan.FromSeconds(2)};
            var clock=Stopwatch.StartNew();
            while(true)
            {
                try { if((await http.GetAsync($"http://127.0.0.1:{port}/v1/status",token)).StatusCode==HttpStatusCode.UpgradeRequired) return; } catch(Exception e) when(e is HttpRequestException or TaskCanceledException) { }
                if(clock.Elapsed>TimeSpan.FromSeconds(90)) { lock(output) throw new Exception($"FAIL: private wrangler dev did not start on port {port}:\n"+string.Join("\n",output)); }
                await Task.Delay(250,token);
            }
        }
        void Stop(Process? process) { if(process==null) return; try { if(!process.HasExited) process.Kill(true); process.WaitForExit(15000); } catch(Exception e) when(e is InvalidOperationException or System.ComponentModel.Win32Exception) { } process.Dispose(); }
        var restarting=new IdentityStore(Path.Combine(root,"restart-host")); restarting.Config.RelayUrl=$"ws://127.0.0.1:{port}";
        Process? wrangler=Launch();
        try
        {
            await Ready();
            using var presence=new RelayPresence(restarting,()=>[],(stream,_)=> { stream.Dispose(); return Task.CompletedTask; });
            presence.Start(); await Until(()=>presence.State==Online,TimeSpan.FromSeconds(15));
            Stop(wrangler); wrangler=null;
            bool dropped=await Reached(()=>presence.State!=Online,TimeSpan.FromSeconds(15));
            wrangler=Launch(); await Ready();
            var back=Stopwatch.StartNew();
            bool returned=await Reached(()=>presence.State==Online,TimeSpan.FromSeconds(10));
            Console.WriteLine($"Relay restart: presence online {back.Elapsed.TotalSeconds:F1} s after the relay came back.");
            check(dropped && returned,"presence reconnects after relay restart");
        }
        finally { Stop(wrangler); }
    }
}
