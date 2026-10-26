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
    static byte[] RandomFile(string path,int bytes) { var data=RandomNumberGenerator.GetBytes(bytes); File.WriteAllBytes(path,data); return data; }
    public static async Task Run(string root,Action<bool,string> check)
    {
        var host=new IdentityStore(Path.Combine(root,"file-host")); var controller=new IdentityStore(Path.Combine(root,"file-controller"));
        string receive=Path.Combine(root,"file-receive"), local=Path.Combine(root,"file-local"), source=Path.Combine(root,"file-source");
        Directory.CreateDirectory(source); Directory.CreateDirectory(local);
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

                bool missing=false; try { await client.List(Path.Combine(source,"gone"),lifetime.Token); } catch(InvalidOperationException) { missing=true; }
                check(missing,"list of a missing folder is refused");
                Directory.CreateDirectory(Path.Combine(source,"Docs"));
                var listing=await client.List(source,lifetime.Token);
                check(listing.Folder==source && listing.Entries.Any(e=>e.Name=="Docs" && e.Folder) && listing.Entries.Any(e=>e.Name=="photo.jpg" && !e.Folder && e.Size==photo.Length),"list shows a folder");
                var roots=await client.List("",lifetime.Token);
                check(roots.Folder==Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) && roots.Drives.Contains(Path.GetPathRoot(Environment.SystemDirectory)!),"start listing is the user profile with the drives");
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
