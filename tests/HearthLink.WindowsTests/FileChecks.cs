using HearthLink;
using HearthLink.Core;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// File link protocol over an in-memory pinned-TLS pair: FileHost on one end, FileClient (or a hand-driven peer) on the other.
static class FileChecks
{
    sealed class Progress(Action<long> report) : IProgress<long> { public void Report(long value)=>report(value); }
    static async Task<(RelayStream Client,RelayStream Server)> Pipe()
    {
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        try
        {
            var client=new TcpClient(); var accepting=listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
            var server=await accepting;
            return (new RelayStream(WebSocket.CreateFromStream(client.GetStream(),false,null,TimeSpan.FromSeconds(30))),new RelayStream(WebSocket.CreateFromStream(server.GetStream(),true,null,TimeSpan.FromSeconds(30))));
        }
        finally { listener.Stop(); }
    }
    /// <summary>A controller connection and the host's end of it, both past the pinned TLS handshake.</summary>
    public static async Task<(Connection Controller,Connection Host)> Linked(IdentityStore host,IdentityStore controller)
    {
        var (clientTransport,serverTransport)=await Pipe();
        var ssl=new SslStream(serverTransport,false,(_,cert,_,_)=>cert!=null);
        var serving=ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {ServerCertificate=host.Certificate,ClientCertificateRequired=true,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,CertificateRevocationCheckMode=X509RevocationMode.NoCheck});
        var client=await Connection.Secure(clientTransport,controller,host.Fingerprint,null,CancellationToken.None);
        await serving;
        return (client,new Connection(serverTransport,ssl));
    }
    /// <summary>Starts a session on the real host, approving its prompt; frames are acknowledged in the background so the session stays up.</summary>
    static async Task<(Connection Session,Packet Ready,Task Reader)> Session(IdentityStore host,IdentityStore controller,uint uiThread,bool control)
    {
        var c=await Connection.Connect("127.0.0.1",controller,host.Fingerprint);
        await c.Send(new("session",Control:control,Value:Wire.ProtocolVersion));
        await NativeDialogs.Answer(uiThread,NativeDialogs.Yes,TimeSpan.FromSeconds(15));
        var ready=await c.Read();
        var reader=Task.Run(async()=>
        {
            try { while(true) { var p=await c.Read(); if(p.Frame is {} f) await c.Send(new("presented",Sequence:f.Sequence)); } }
            catch(Exception e) when(e is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        return (c,ready,reader);
    }
    static async Task<Connection> Files(IdentityStore host,IdentityStore controller,string? token)
    {
        var f=await Connection.Connect("127.0.0.1",controller,host.Fingerprint);
        await f.Send(new("files",Code:token)); return f;
    }
    public static async Task RunSessions(MainWindow window,IdentityStore host,IdentityStore controller,string receive,string root,Action<bool,string> check)
    {
        uint uiThread=await window.OnUi(NativeDialogs.CurrentThread);
        string source=Path.Combine(root,"session-files"); Directory.CreateDirectory(source);
        string sent=Path.Combine(source,"handover.zip"); byte[] data=RandomFile(sent,1024*1024);
        using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var (session,ready,reader)=await Session(host,controller,uiThread,true);
        string token=ready.Code??"";
        // A failed check must not leave the session open, or the fixture cannot shut down and hides the failure.
        if(!(ready.Kind=="ready" && token.Length==64)) { session.Dispose(); await reader; }
        check(ready.Kind=="ready" && token.Length==64,"control session offers a file grant");
        using(var wrong=await Files(host,controller,new string('0',64))) check((await wrong.Read(limit.Token)).Kind=="denied","wrong token is denied");
        var files=new FileClient(await Files(host,controller,token));
        string saved=await files.Put(sent,null,limit.Token);
        check(saved=="handover.zip" && File.ReadAllBytes(Path.Combine(receive,"handover.zip")).SequenceEqual(data),"files connection with the grant puts a file");
        await session.Send(new("disconnect"));
        bool closed=false;
        try { await files.List("",limit.Token).WaitAsync(TimeSpan.FromSeconds(10)); } catch(Exception e) when(e is IOException or InvalidDataException) { closed=true; }
        files.Dispose(); session.Dispose(); await reader;
        using(var late=await Files(host,controller,token)) check(closed && (await late.Read(limit.Token)).Kind=="denied","files connection closes when the session ends");
        await Task.Delay(500);
        var (viewOnly,viewReady,viewReader)=await Session(host,controller,uiThread,false);
        check(viewReady.Kind=="ready" && viewReady.Code==null,"view-only session offers none");
        await viewOnly.Send(new("disconnect")); viewOnly.Dispose(); await viewReader;
        await Task.Delay(500);
    }
    /// <summary>The file manager's copy engine on the real UI thread: folder trees both ways into chosen folders, never overwriting.</summary>
    public static async Task RunStrip(MainWindow window,IdentityStore host,IdentityStore controller,string root,Action<bool,string> check)
    {
        string near=Path.Combine(root,"strip-local"), far=Path.Combine(root,"strip-remote"), tree=Path.Combine(near,"Trip");
        Directory.CreateDirectory(Path.Combine(tree,"Day 1")); Directory.CreateDirectory(Path.Combine(tree,"Empty")); Directory.CreateDirectory(far);
        File.WriteAllText(Path.Combine(tree,"cover.jpg"),"cover"); File.WriteAllText(Path.Combine(tree,"Day 1","beach.jpg"),"beach!"); File.WriteAllText(Path.Combine(near,"loose.txt"),"x");
        async Task<Connection> Open(CancellationToken t) { var (c,h)=await Linked(host,controller); _=new FileHost(Path.Combine(root,"strip-unused")).Serve(h,CancellationToken.None); return c; }
        bool Same(string a,string b)=>Directory.GetFiles(a,"*",SearchOption.AllDirectories).Select(f=>Path.GetRelativePath(a,f)+"="+File.ReadAllText(f)).Order().SequenceEqual(Directory.GetFiles(b,"*",SearchOption.AllDirectories).Select(f=>Path.GetRelativePath(b,f)+"="+File.ReadAllText(f)).Order())
            && Directory.GetDirectories(a,"*",SearchOption.AllDirectories).Select(d=>Path.GetRelativePath(a,d)).Order().SequenceEqual(Directory.GetDirectories(b,"*",SearchOption.AllDirectories).Select(d=>Path.GetRelativePath(b,d)).Order());
        var strip=await window.OnUi(()=>new TransferStrip(Open));
        await (await window.OnUi(()=>strip.Send([tree,Path.Combine(near,"loose.txt")],far)));
        await (await window.OnUi(()=>strip.Send([tree],far)));
        check(Same(tree,Path.Combine(far,"Trip")) && Same(tree,Path.Combine(far,"Trip (2)")) && File.ReadAllText(Path.Combine(far,"loose.txt"))=="x","copy to the remote pane recreates folder trees in its folder");
        string back=Path.Combine(root,"strip-back"); Directory.CreateDirectory(back);
        await (await window.OnUi(()=>strip.Get([(Path.Combine(far,"Trip"),true,0L),(Path.Combine(far,"loose.txt"),false,1L)],back)));
        await (await window.OnUi(()=>strip.Get([(Path.Combine(far,"Trip"),true,0L)],back)));
        check(Same(tree,Path.Combine(back,"Trip")) && Same(tree,Path.Combine(back,"Trip (2)")) && File.ReadAllText(Path.Combine(back,"loose.txt"))=="x","copy to the local pane recreates remote folder trees in its folder");
        await window.OnUi(()=> { strip.Dispose(); return true; });
    }
    static byte[] RandomFile(string path,int bytes) { var data=RandomNumberGenerator.GetBytes(bytes); File.WriteAllBytes(path,data); return data; }
    public static async Task Run(string root,Action<bool,string> check)
    {
        var host=new IdentityStore(Path.Combine(root,"file-host")); var controller=new IdentityStore(Path.Combine(root,"file-controller"));
        string receive=Path.Combine(root,"file-receive"), local=Path.Combine(root,"file-local"), source=Path.Combine(root,"file-source");
        Directory.CreateDirectory(source); Directory.CreateDirectory(local);
        {
            string dropped=Path.Combine(root,"dropped"), album=Path.Combine(dropped,"Album");
            Directory.CreateDirectory(Path.Combine(album,"Sub")); Directory.CreateDirectory(Path.Combine(album,"Empty"));
            File.WriteAllText(Path.Combine(album,"a.txt"),"aa"); File.WriteAllText(Path.Combine(album,"Sub","b.txt"),"b"); string loose=Path.Combine(dropped,"c.txt"); File.WriteAllText(loose,"ccc");
            var drop=TransferStrip.Expand([album,loose,Path.Combine(dropped,"gone.txt")]);
            check(drop.Select(o=>o.Relative).SequenceEqual(["","a.txt","Empty","Sub","Sub\\b.txt","c.txt"]) && drop.Select(o=>o.Local==null).SequenceEqual([true,false,true,true,false,false])
                && drop.Take(5).All(o=>o.Root==0 && o.RootName=="Album") && drop[5].Root==-1 && drop[1].Size==2 && drop[5].Size==3,"a dropped folder expands into its folders and files, folder first");
        }
        using var lifetime=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        async Task<(FileClient Client,Task Serving,Connection Host)> Open()
        {
            var (c,h)=await Linked(host,controller);
            return (new FileClient(c),new FileHost(receive).Serve(h,lifetime.Token),h);
        }

        {
            var (client,serving,_)=await Open();
            using(client)
            {
                string sent=Path.Combine(source,"report.pdf"); byte[] data=RandomFile(sent,3*1024*1024);
                long reported=0; string saved=await client.Put(sent,new Progress(v=>reported=v),lifetime.Token);
                check(saved=="report.pdf" && File.ReadAllBytes(Path.Combine(receive,"report.pdf")).SequenceEqual(data) && reported==data.Length,"put saves into the receive folder");
                string again=await client.Put(sent,null,lifetime.Token);
                check(again=="report (2).pdf" && File.Exists(Path.Combine(receive,"report (2).pdf")) && File.ReadAllBytes(Path.Combine(receive,"report.pdf")).SequenceEqual(data),"put never overwrites");

                string top=await client.Folder("Album",lifetime.Token), duplicate=await client.Folder("Album",lifetime.Token);
                check(top=="Album" && duplicate=="Album (2)" && Directory.Exists(Path.Combine(receive,"Album (2)")),"a sent folder never merges into an existing one");
                string nested=await client.Folder("Album\\2026\\Empty",lifetime.Token);
                string inFolder=await client.Put(sent,null,lifetime.Token,"Album\\2026\\report.pdf");
                check(nested=="Album\\2026\\Empty" && Directory.Exists(Path.Combine(receive,"Album","2026","Empty")) && inFolder=="Album\\2026\\report.pdf" && File.ReadAllBytes(Path.Combine(receive,"Album","2026","report.pdf")).SequenceEqual(data),"files and empty subfolders keep their place inside a sent folder");
                string chosen=Path.Combine(root,"chosen-folder"); Directory.CreateDirectory(chosen);
                string intoChosen=await client.Put(sent,null,lifetime.Token,null,chosen), intoChosenAgain=await client.Put(sent,null,lifetime.Token,null,chosen);
                string chosenAlbum=await client.Folder("Album",lifetime.Token,chosen);
                check(intoChosen=="report.pdf" && intoChosenAgain=="report (2).pdf" && File.ReadAllBytes(Path.Combine(chosen,"report.pdf")).SequenceEqual(data) && chosenAlbum=="Album" && Directory.Exists(Path.Combine(chosen,"Album")),"put and folder go into a chosen host folder without overwriting");
                bool relativeBase=false, missingBase=false;
                try { await client.Put(sent,null,lifetime.Token,null,"chosen-folder"); } catch(InvalidOperationException) { relativeBase=true; }
                try { await client.Put(sent,null,lifetime.Token,null,Path.Combine(root,"no-such-folder")); } catch(InvalidOperationException) { missingBase=true; }
                check(relativeBase && missingBase && !Directory.Exists(Path.Combine(root,"no-such-folder")),"a chosen host folder must be an existing absolute folder");
                string hostFile=Path.Combine(source,"photo.jpg"); byte[] photo=RandomFile(hostFile,2*1024*1024+512*1024);
                string got=await client.Get(hostFile,local,null,lifetime.Token);
                check(got==Path.Combine(local,"photo.jpg") && File.ReadAllBytes(got).SequenceEqual(photo),"get copies a host file");

                string locked=Path.Combine(source,"locked.txt"); File.WriteAllText(locked,"busy");
                bool lockedDenied=false;
                using(new FileStream(locked,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { try { await client.Get(locked,local,null,lifetime.Token); } catch(InvalidOperationException) { lockedDenied=true; } }
                string huge=Path.Combine(source,"huge.bin");
                using(var stream=new FileStream(huge,FileMode.Create)) stream.SetLength(FilePolicy.MaxFileBytes+1);
                bool hugeDenied=false; try { await client.Get(huge,local,null,lifetime.Token); } catch(InvalidOperationException) { hugeDenied=true; }
                File.Delete(huge);
                check(lockedDenied && hugeDenied && !File.Exists(Path.Combine(local,"locked.txt")) && !File.Exists(Path.Combine(local,"huge.bin")),"get refuses an oversized or unreadable file");

                string trip=Path.Combine(source,"Trip"); Directory.CreateDirectory(Path.Combine(trip,"Day 1")); Directory.CreateDirectory(Path.Combine(trip,"Empty"));
                File.WriteAllText(Path.Combine(trip,"cover.jpg"),"cover"); File.WriteAllText(Path.Combine(trip,"Day 1","beach.jpg"),"beach!");
                var remote=await TransferStrip.ExpandRemote(client,[(trip,true,0L),(hostFile,false,(long)photo.Length)],lifetime.Token);
                check(remote.Select(i=>i.Relative).SequenceEqual(["","cover.jpg","Day 1","Day 1\\beach.jpg","Empty","photo.jpg"]) && remote.Select(i=>i.Remote==null).SequenceEqual([true,false,true,false,true,false])
                    && remote[3].Remote==Path.Combine(trip,"Day 1","beach.jpg") && remote[3].Size==6 && remote.Take(5).All(i=>i.RootName=="Trip" && i.Root==0) && remote[5].Root==-1,"a picked host folder expands into its folders and files, folder first");
                bool missing=false; try { await client.List(Path.Combine(source,"gone"),lifetime.Token); } catch(InvalidOperationException) { missing=true; }
                check(missing,"list of a missing folder is refused");
                Directory.CreateDirectory(Path.Combine(source,"Docs"));
                var listing=await client.List(source,lifetime.Token);
                check(listing.Folder==source && listing.Entries.Any(e=>e.Name=="Docs" && e.Folder) && listing.Entries.Any(e=>e.Name=="photo.jpg" && !e.Folder && e.Size==photo.Length),"list shows a folder");
                var roots=await client.List("",lifetime.Token);
                check(roots.Folder==Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) && roots.Drives.Any(d=>d.Root==Path.GetPathRoot(Environment.SystemDirectory) && d.Total>0 && d.Free<=d.Total),"start listing is the user profile with the drives");
            }
            await serving.WaitAsync(TimeSpan.FromSeconds(10));
        }
        {
            var (c,h)=await Linked(host,controller);
            using(c) using(h)
            {
                // Raw request: a real client never sends this, but a hostile one could.
                await c.Send(new("put",Name:"..\\escape.txt",Sequence:4));
                var serving=new FileHost(receive).Serve(h,lifetime.Token);
                var reply=await c.Read();
                check(reply.Kind=="denied" && !File.Exists(Path.Combine(root,"escape.txt")) && Directory.GetFiles(receive).Length==2,"put rejects unsafe names");
                await c.Send(new("put",Name:"Album\\..\\..\\climb.txt",Sequence:4)); var climb=await c.Read();
                await c.Send(new("folder",Name:"..")); var up=await c.Read();
                await c.Send(new("folder",Name:"C:\\Windows")); var absolute=await c.Read();
                check(climb.Kind=="denied" && up.Kind=="denied" && absolute.Kind=="denied" && !File.Exists(Path.Combine(root,"climb.txt")),"folder paths cannot leave the receive folder");
            }
        }
        {
            var (c,h)=await Linked(host,controller);
            using var client=new FileClient(c); using(h)
            {
                byte[] data=RandomNumberGenerator.GetBytes(100*1024);
                var getting=client.Get(Path.Combine(source,"fake.bin"),local,null,lifetime.Token);
                await h.Read();
                await h.Send(new("file",Name:"tampered.bin",Sequence:data.Length));
                var id=Guid.NewGuid();
                for(int offset=0;offset<data.Length;offset+=FilePolicy.ChunkBytes) await h.Send(new("file-chunk",Chunk:new(id,offset,data[offset..Math.Min(data.Length,offset+FilePolicy.ChunkBytes)])));
                await h.Send(new("file-end",Code:new string('0',64)));
                bool rejected=false; try { await getting; } catch(InvalidDataException) { rejected=true; }
                check(rejected && !Directory.EnumerateFiles(local,"tampered*").Any() && !Directory.EnumerateFiles(local,"*.partial").Any(),"tampered digest leaves no file");
            }
        }
        {
            var (client,serving,hostEnd)=await Open();
            string big=Path.Combine(source,"big.bin"); RandomFile(big,8*1024*1024);
            using var cancel=new CancellationTokenSource();
            bool cancelled=false;
            try { await client.Get(big,local,new Progress(v=> { if(v>1024*1024) cancel.Cancel(); }),cancel.Token); } catch(OperationCanceledException) { cancelled=true; }
            client.Dispose(); hostEnd.Dispose();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(10)); } catch(Exception e) when(e is not TimeoutException) { }
            check(cancelled && !File.Exists(Path.Combine(local,"big.bin")) && !Directory.EnumerateFiles(local,"*.partial").Any(),"cancel removes the partial file");
        }
    }
}
