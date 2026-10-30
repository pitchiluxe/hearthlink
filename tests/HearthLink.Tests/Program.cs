using HearthLink.Core;
using System.Buffers.Binary;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
var gate = new PairingGate();
Check(!gate.Consume("00000000", DateTimeOffset.UtcNow), "pairing closed by default");
var now = DateTimeOffset.UtcNow;
string code = gate.Open(now);
Check(code.Length == 8 && code.All(char.IsAsciiDigit), "eight random decimal digits");
Check(!gate.Consume("wrong", now), "wrong code rejected");
Check(gate.Consume(code, now), "valid code accepted");
Check(!gate.Consume(code, now), "code cannot be replayed");
code = gate.Open(now);
Check(!gate.Consume(code, now.AddMinutes(3)), "expired code rejected");
code = gate.Open(now);
for (int i=0;i<5;i++) gate.Consume("wrong", now, "10.0.0.9");
Check(!gate.Consume(code, now, "10.0.0.9"), "five failures lock out the guessing peer");
Check(gate.Consume(code, now, "10.0.0.2"), "another peer's failures do not close the pairing window");
code = gate.Open(now);
for (int i=0;i<PairingGate.TotalAttempts;i++) gate.Consume("wrong", now, "10.0.1." + (i/PairingGate.PeerAttempts));
Check(!gate.Consume(code, now, "10.0.0.2"), "total failures across peers close pairing window");
Check(PeerName.Clean("Desk\u202Etxt.exe\u200B") == "Desktxt.exe", "peer name strips bidi override and zero-width characters");
Check(PeerName.Clean("A\u2028B\u2029C\r\n") == "ABC", "peer name strips line and paragraph separators");
Check(PeerName.Clean(new string('x', 100)).Length == PeerName.MaxLength, "peer name is length-limited");
Check(!PeerName.Valid("Desk\u202E") && !PeerName.Valid("") && !PeerName.Valid(" Desk") && PeerName.Valid("Office PC"), "pairing accepts only names that are already clean");
var trust = new TrustBook();
Check(!trust.Contains("A"), "unknown controller rejected");
trust.Add("A", "Desk"); Check(trust.Contains("A"), "authorized controller accepted");
trust.Remove("A"); Check(!trust.Contains("A"), "revoked controller rejected");
using var stream = new MemoryStream();
await Wire.Write(stream, new Packet("hello", Text:"actual message"));
stream.Position=0;
Check((await Wire.Read(stream)).Text == "actual message", "framing round trip");
using var malformed = new MemoryStream(new byte[4]);
bool rejected=false;
try { await Wire.Read(malformed); } catch(InvalidDataException) { rejected=true; }
Check(rejected,"empty frame rejected");
byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, Wire.MaxLength+1);
rejected=false;
try { await Wire.Read(new MemoryStream(header)); } catch(InvalidDataException) { rejected=true; }
Check(rejected,"oversized frame rejected before allocation");
byte[] jpegFixture=Enumerable.Range(0,4096).Select(i=>(byte)(i%256)).ToArray();
using var videoStream=new MemoryStream();
await Wire.Write(videoStream,new("frame",Frame:new(jpegFixture,1920,1080,0,17,1200,2300,8)));
videoStream.Position=0;
var video=await Wire.Read(videoStream);
Check(video.Frame!=null && video.Frame.Sequence==17 && video.Frame.Jpeg.SequenceEqual(jpegFixture),"video frame round trips without Base64");
Check(videoStream.Length==jpegFixture.Length+40,"video frame has only 40 bytes of binary framing overhead");
Check(video.Frame!.Width==1920 && video.Frame.EncodeMicroseconds==2300,"binary frame dimensions and timings preserved");
byte[] damaged=videoStream.ToArray(); BinaryPrimitives.WriteInt32BigEndian(damaged.AsSpan(16),100000);
rejected=false; try {await Wire.Read(new MemoryStream(damaged));} catch(InvalidDataException) {rejected=true;}
Check(rejected,"oversized video dimensions rejected before JPEG allocation");
using(var window=new FrameWindow())
{
    var sent=new List<long>();
    for(int i=0;i<FrameWindow.Depth;i++) { long s=await window.AcquireAsync(); window.MarkSent(s); sent.Add(s); }
    var blocked=window.AcquireAsync();
    Check(FrameWindow.Depth==3 && !blocked.IsCompleted,"three frames may await presentation, a fourth waits");
    Check(!window.Acknowledge(sent[^1]+1),"acknowledgement of an unsent frame ignored");
    // The viewer paints only the newest frame, so its acknowledgement covers every earlier one.
    Check(window.Acknowledge(sent[1]),"presentation acknowledgement accepted");
    long fourth=await blocked; window.MarkSent(fourth);
    var fifth=window.AcquireAsync();
    Check(fifth.IsCompleted,"acknowledgement releases the acknowledged frame and all earlier ones");
    long abandoned=await fifth; window.Abandon(abandoned);
    var sixth=window.AcquireAsync();
    Check(sixth.IsCompleted && !window.Acknowledge(sent[0]) && !window.Acknowledge(abandoned),"abandoned and already acknowledged frames are ignored");
    window.MarkSent(await sixth);
    using var cancel=new CancellationTokenSource(); var waiting=window.AcquireAsync(cancel.Token); cancel.Cancel();
    bool cancelled=false; try {await waiting;} catch(OperationCanceledException) {cancelled=true;}
    Check(cancelled,"waiting video producer cancels cleanly");
}
var input=new InputMailbox(4);
for(int i=0;i<10000;i++) CheckQuiet(input.Post(new("move",X:i)));
Check((await input.ReadAsync()).X==9999,"mouse motion coalesces to latest position");
input.Post(new("move",X:7)); input.Post(new("button",Value:1,Down:true)); input.Post(new("button",Value:1));
Check((await input.ReadAsync()).X==7 && (await input.ReadAsync()).Down && !(await input.ReadAsync()).Down,"click retains its preceding pointer position and release ordering");
for(int i=0;i<4;i++) CheckQuiet(input.Post(new("key",Value:65,Down:true)));
Check(!input.Post(new("key",Value:66)),"input queue is bounded during congestion");
Check(SessionNoticeText.Message("Controller\r\nInjected",false)=="ControllerInjected is connected\nView only","connection notification sanitizes controller label and identifies view-only mode");
Check(SessionNoticeText.Message("Desk",true).Contains("Keyboard and mouse control"),"connection notification distinguishes control access");
Check(CaptureSizing.Fit(3840,2160,1280,720)==(1280,720),"responsive mode caps encoded pixels while preserving aspect ratio");
Check(CaptureSizing.Fit(1024,768,1600,900)==(1024,768),"capture never upscales a smaller monitor");
Check(CaptureSizing.Fit(1080,1920,1280,720)==(405,720),"portrait monitor sizing preserves aspect ratio");
Check(!FilePolicy.ValidName("..\\secret.txt") && !FilePolicy.ValidName("../secret.txt"),"remote file names cannot traverse directories");
Check(!FilePolicy.ValidName("C:\\file.txt") && !FilePolicy.ValidName("CON.txt") && !FilePolicy.ValidName("name. "),"remote file names reject absolute paths and Windows reserved names");
Check(FilePolicy.ValidName("Project report.pdf"),"ordinary file name accepted");
{
    string folder=Path.Combine(Path.GetTempPath(),"hl-names-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    try
    {
        File.WriteAllText(Path.Combine(folder,"report.pdf"),"1"); File.WriteAllText(Path.Combine(folder,"report (2).pdf"),"2"); File.WriteAllText(Path.Combine(folder,"notes"),"3");
        Check(FileNames.Unique(folder,"report.pdf")==Path.Combine(folder,"report (3).pdf") && FileNames.Unique(folder,"notes")==Path.Combine(folder,"notes (2)") && FileNames.Unique(folder,"new.txt")==Path.Combine(folder,"new.txt"),"unique name never overwrites");
        bool refused=false; try { FileNames.Unique(folder,"..\\x.txt"); } catch(InvalidDataException) { refused=true; }
        Check(refused,"unique name refuses unsafe names");
    }
    finally { Directory.Delete(folder,true); }
    Check(FileNames.SplitRelative("Album\\2026\\photo.jpg").SequenceEqual(["Album","2026","photo.jpg"]) && FileNames.SplitRelative("notes.txt").SequenceEqual(["notes.txt"]),"relative paths split into safe segments");
    bool Refused(string path) { try { FileNames.SplitRelative(path); return false; } catch(InvalidDataException) { return true; } }
    Check(Refused("Album\\..\\x.txt") && Refused("..\\x.txt") && Refused("C:\\x.txt") && Refused("\\x.txt") && Refused("Album\\\\x.txt") && Refused("Album/x.txt") && Refused("") && Refused(string.Join("\\",Enumerable.Repeat("d",33))),"relative paths cannot escape, be absolute, be empty or nest too deep");
    var drives=FileNames.DecodeDrives(FileNames.EncodeDrives([new("C:\\","Storage",27_400_000_000,465_700_000_000),new("D:\\","",677_700_000_000,919_700_000_000)])+"");
    Check(drives.Count==2 && drives[0]==new DriveEntry("C:\\","Storage",27_400_000_000,465_700_000_000) && drives[1].Root=="D:\\","drive list with free space round trips");
    Check(FileNames.DecodeDrives("[{\"Root\":\"C:\\\\Windows\",\"Label\":\"\",\"Free\":1,\"Total\":2},{\"Root\":\"E:\\\\\",\"Label\":\"\",\"Free\":-1,\"Total\":2}]").Count==0 && FileNames.DecodeDrives("not json").Count==0,"drive list drops entries that are not drive roots");
    var when=new DateTimeOffset(2026,10,25,12,0,0,TimeSpan.Zero);
    string json=FileNames.Encode([new("a.txt",false,12,when),new("Docs",true,0,when)]).Replace("]",",{\"Name\":\"..\\\\evil\",\"Folder\":false,\"Size\":1,\"Modified\":\"2026-10-25T12:00:00+00:00\"}]");
    var entries=FileNames.Decode(json);
    Check(entries.Count==2 && entries[0]==new FileEntry("a.txt",false,12,when) && entries[1].Folder,"listing round trips and drops unsafe names");
}
{
    byte[] nonce=Enumerable.Range(1,32).Select(i=>(byte)i).ToArray();
    byte[] expected=System.Text.Encoding.UTF8.GetBytes("hearthlink-relay-v1\n/v1/presence\n").Concat(nonce).ToArray();
    Check(RelayProtocol.SignatureInput(RelayProtocol.Presence,nonce).SequenceEqual(expected) && !RelayProtocol.SignatureInput(RelayProtocol.Status,nonce).SequenceEqual(expected),"relay signature input is domain separated");
    Check(new PairingCode("0420","12345678").Display=="0420-1234-5678" && PairingCode.TryParse("0420 1234 5678",out var spaced) && spaced.Lookup=="0420" && spaced.Secret=="12345678" && PairingCode.TryParse("042012345678",out _)
        && !PairingCode.TryParse("0420-1234-567",out _) && !PairingCode.TryParse("abcd-1234-5678",out _) && PairingCode.New("12345678").Lookup.Length==4,"pairing code display and parse");
    var (a,b)=await RelayPair();
    using(var left=new RelayStream(a)) using(var right=new RelayStream(b))
    {
        byte[] payload=new byte[Wire.MaxLength]; Random.Shared.NextBytes(payload);
        var send=left.WriteAsync(payload).AsTask();
        var sizes=new List<int>(); byte[] got=new byte[payload.Length]; int total=0,message=0; byte[] buffer=new byte[100000];
        while(total<payload.Length)
        {
            var r=await b.ReceiveAsync(buffer,CancellationToken.None); message+=r.Count;
            Buffer.BlockCopy(buffer,0,got,total,r.Count); total+=r.Count;
            if(r.EndOfMessage) {sizes.Add(message); message=0;}
        }
        await send;
        Check(got.AsSpan().SequenceEqual(payload) && sizes.Max()<=RelayProtocol.MessageBytes && sizes.Count==256,"16 MB payload round-trips through 64 KiB messages");
    }
    var (c,d)=await RelayPair();
    using(var left=new RelayStream(c)) using(var right=new RelayStream(d))
    {
        byte[] bulk=new byte[200000]; Random.Shared.NextBytes(bulk); var w=left.WriteAsync(bulk).AsTask();
        byte[] small=new byte[1000]; byte[] all=new byte[bulk.Length]; int n=0;
        while(n<all.Length) n+=await right.ReadAsync(all.AsMemory(n,Math.Min(small.Length,all.Length-n)));
        await w;
        Check(all.AsSpan().SequenceEqual(bulk),"relay stream handles reads smaller than a message");
    }
    var (e,f)=await RelayPair();
    using(var server=new RelayStream(e)) using(var client=new RelayStream(f))
    {
        using var serverCert=TestCertificate("server"); using var clientCert=TestCertificate("client");
        using var ssl1=new System.Net.Security.SslStream(server,false,(_,cert,_,_)=>cert!=null);
        using var ssl2=new System.Net.Security.SslStream(client,false,(_,cert,_,_)=>cert!=null);
        var serverAuth=ssl1.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions{ServerCertificate=serverCert,ClientCertificateRequired=true,EnabledSslProtocols=System.Security.Authentication.SslProtocols.Tls12|System.Security.Authentication.SslProtocols.Tls13});
        var clientAuth=ssl2.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions{TargetHost="hearthlink",ClientCertificates=new System.Security.Cryptography.X509Certificates.X509CertificateCollection{clientCert},EnabledSslProtocols=System.Security.Authentication.SslProtocols.Tls12|System.Security.Authentication.SslProtocols.Tls13});
        await Task.WhenAll(serverAuth,clientAuth);
        await Wire.Write(ssl2,new("hello",Text:"through relay")); var packet=await Wire.Read(ssl1);
        Check(packet.Text=="through relay" && ssl1.RemoteCertificate!=null && ssl2.RemoteCertificate!=null,"relay stream carries mutual TLS");
    }
    var (g,h)=await RelayPair();
    using(var closing=new RelayStream(g)) using(var closed=new RelayStream(h))
    {
        closing.Dispose(); Check(await closed.ReadAsync(new byte[16])==0,"closed relay stream reads zero");
    }
    var (i,j)=await RelayPair();
    using(var reader=new RelayStream(j))
    {
        await i.SendAsync(new byte[]{65},System.Net.WebSockets.WebSocketMessageType.Text,true,CancellationToken.None);
        bool textRejected=false; try {await reader.ReadExactlyAsync(new byte[16]);} catch(InvalidDataException) {textRejected=true;}
        Check(textRejected,"relay stream rejects text frames");
    }
    System.Net.Sockets.TcpClient? dropped=null; var (k,l)=await RelayPair(t=>dropped=t);
    using(var survivor=new RelayStream(l))
    {
        dropped!.Client.LingerState=new System.Net.Sockets.LingerOption(true,0); dropped.Dispose();
        Check(await survivor.ReadAsync(new byte[16])==0,"abrupt peer disconnect reads zero");
    }
    var (m,o)=await RelayPair();
    using(var closer=new RelayStream(o)) using(var peer=m)
    {
        await peer.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,null,CancellationToken.None);
        Check(await closer.ReadAsync(new byte[16])==0 && await closer.ReadAsync(new byte[16])==0,"reads after peer close keep returning zero");
    }
    var (q,rr)=await RelayPair();
    using(var sizeReader=new RelayStream(rr))
    {
        await q.SendAsync(new byte[RelayProtocol.MessageBytes+1],System.Net.WebSockets.WebSocketMessageType.Binary,true,CancellationToken.None);
        bool tooBig=false; try {await sizeReader.ReadExactlyAsync(new byte[100000]);} catch(InvalidDataException) {tooBig=true;}
        Check(tooBig,"relay stream rejects an oversize message");
    }
    var (u,v)=await RelayPair();
    using(var splitReader=new RelayStream(v))
    {
        await u.SendAsync(new byte[40000],System.Net.WebSockets.WebSocketMessageType.Binary,false,CancellationToken.None);
        await u.SendAsync(new byte[30000],System.Net.WebSockets.WebSocketMessageType.Binary,true,CancellationToken.None);
        bool tooBigSplit=false; try {await splitReader.ReadExactlyAsync(new byte[40000]); await splitReader.ReadExactlyAsync(new byte[30000]);} catch(InvalidDataException) {tooBigSplit=true;}
        Check(tooBigSplit,"relay stream rejects an oversize message split across continuation frames");
    }
}
static async Task<(System.Net.WebSockets.WebSocket,System.Net.WebSockets.WebSocket)> RelayPair(Action<System.Net.Sockets.TcpClient>? serverTcp=null)
{
    var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0); listener.Start();
    var accept=listener.AcceptTcpClientAsync(); var client=new System.Net.Sockets.TcpClient(); await client.ConnectAsync(System.Net.IPAddress.Loopback,((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
    var server=await accept; listener.Stop(); serverTcp?.Invoke(server);
    return (System.Net.WebSockets.WebSocket.CreateFromStream(server.GetStream(),true,null,TimeSpan.FromSeconds(30)),System.Net.WebSockets.WebSocket.CreateFromStream(client.GetStream(),false,null,TimeSpan.FromSeconds(30)));
}
static System.Security.Cryptography.X509Certificates.X509Certificate2 TestCertificate(string name)
{
    using var rsa=System.Security.Cryptography.RSA.Create(2048);
    var request=new System.Security.Cryptography.X509Certificates.CertificateRequest("CN="+name,rsa,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    using var ephemeral=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
    return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(ephemeral.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx),null,System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.UserKeySet);
}
UnattendedChecks.Run(Check);
HistoryChecks.Run(Check);
await UpdateChecks.Run(Check);
await AssistantChecks.Run(Check);
Console.WriteLine($"{passed} checks passed.");
void CheckQuiet(bool value) {if(!value) throw new Exception("Fixture enqueue failed");}
