using HearthLink.Core;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace HearthLink;
/// <summary>A relay refusal or failure. <see cref="CloseCode"/> is the relay's WebSocket close code, or 0 when the relay could not be reached.</summary>
public sealed class RelayException(int closeCode,string message) : Exception(message)
{
    public int CloseCode { get; }=closeCode;
    public static RelayException For(int closeCode)=>new(closeCode,closeCode switch
    {
        0=>"Cannot reach the HearthLink relay. Check the internet connection.",
        RelayProtocol.BadMessage=>"The relay rejected a malformed message.",
        RelayProtocol.NotAuthenticated=>"The relay could not verify this PC's identity.",
        RelayProtocol.Forbidden=>"This PC is not paired with that computer.",
        RelayProtocol.Offline=>"That computer is offline or the pairing code has expired.",
        RelayProtocol.Timeout=>"That computer did not answer. Check HearthLink is running there.",
        RelayProtocol.TooMany=>"Too many pairing attempts from this network. Wait 10 minutes and try again.",
        _=>"The relay closed the connection unexpectedly."
    });
}
public static class RelayClient
{
    public const string DefaultUrl="wss://hearthlink-relay.erickomari.workers.dev";
    static readonly TimeSpan HandshakeLimit=TimeSpan.FromSeconds(15), ReadyLimit=TimeSpan.FromSeconds(25), DirectFirst=TimeSpan.FromSeconds(6);
    /// <summary>The relay's base URL joined with an endpoint path and query. Only wss:// is accepted, except ws:// on loopback for local testing.</summary>
    public static Uri Endpoint(string relayUrl,string endpointWithQuery)
    {
        if(!Uri.TryCreate(relayUrl?.Trim(),UriKind.Absolute,out var root) || root.UserInfo.Length>0 || root.Query.Length>0 || root.Fragment.Length>0 || root.AbsolutePath!="/")
            throw new ArgumentException("The relay address must look like wss://relay.example.com.",nameof(relayUrl));
        bool local=root.Scheme=="ws" && (root.Host=="127.0.0.1" || root.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase));
        if(root.Scheme!="wss" && !local) throw new ArgumentException("The relay address must use wss:// (ws:// only for 127.0.0.1 or localhost).",nameof(relayUrl));
        if(!endpointWithQuery.StartsWith("/v1/",StringComparison.Ordinal)) throw new ArgumentException("Unknown relay endpoint.",nameof(endpointWithQuery));
        return new(root.GetLeftPart(UriPartial.Authority)+endpointWithQuery);
    }
    internal static bool IsFingerprint(string? id)=>id is {Length:64} && id.All(c=>char.IsAsciiDigit(c) || c is >='A' and <='F');
    /// <summary>Opens an authenticated relay socket: answers the challenge with this PC's certificate and waits for "ok".</summary>
    public static async Task<ClientWebSocket> Open(string relayUrl,string endpointWithQuery,IdentityStore identity,CancellationToken token)
    {
        var uri=Endpoint(relayUrl,endpointWithQuery);
        string path=endpointWithQuery.Split('?',2)[0];
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(HandshakeLimit);
        ClientWebSocket socket;
        try { socket=await Dial(uri,HttpClient.DefaultProxy,limit.Token); }
        catch(Exception e) when(e is WebSocketException or HttpRequestException or IOException || e is OperationCanceledException && !token.IsCancellationRequested) { throw RelayException.For(0); }
        try
        {
            var buffer=new byte[4096];
            using var challenge=Parse(await Receive(socket,buffer,limit.Token,token));
            if(Type(challenge)!="challenge" || !challenge.RootElement.TryGetProperty("nonce",out var nonceText) || nonceText.ValueKind!=JsonValueKind.String) throw RelayException.For(RelayProtocol.BadMessage);
            byte[] nonce; try { nonce=Convert.FromBase64String(nonceText.GetString()!); } catch(FormatException) { throw RelayException.For(RelayProtocol.BadMessage); }
            using var key=identity.Certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("This PC's identity has no RSA private key.");
            byte[] signature=key.SignData(RelayProtocol.SignatureInput(path,nonce),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            await Send(socket,JsonSerializer.Serialize(new {t="auth",cert=Convert.ToBase64String(identity.Certificate.RawData),sig=Convert.ToBase64String(signature)}),limit.Token,token);
            using var ok=Parse(await Receive(socket,buffer,limit.Token,token));
            if(Type(ok)!="ok") throw RelayException.For(RelayProtocol.BadMessage);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
    /// <summary>
    /// Connects the WebSocket directly first, then through <paramref name="systemProxy"/> only if direct fails. A Windows user proxy
    /// (free "VPN" apps set one) can stall or drop relay sockets, and the relay gains nothing from it: sessions are end-to-end TLS.
    /// Networks that allow only a proxy still work through the fallback.
    /// </summary>
    public static async Task<ClientWebSocket> Dial(Uri uri,IWebProxy? systemProxy,CancellationToken token)
    {
        // Proxies are looked up by the HTTP form of the address, as the WebSocket handshake does.
        var lookup=new UriBuilder(uri) {Scheme=uri.Scheme=="wss"?"https":"http",Port=uri.Port}.Uri;
        bool proxied;
        try { proxied=systemProxy!=null && !systemProxy.IsBypassed(lookup) && systemProxy.GetProxy(lookup) is {} via && via!=lookup; }
        catch(Exception) { proxied=false; }
        var direct=new ClientWebSocket(); direct.Options.Proxy=null;
        using(var first=CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            if(proxied) first.CancelAfter(DirectFirst);
            try { await direct.ConnectAsync(uri,first.Token); return direct; }
            catch(Exception e) when(proxied && (e is WebSocketException or HttpRequestException or IOException || e is OperationCanceledException && !token.IsCancellationRequested)) { direct.Dispose(); }
            catch { direct.Dispose(); throw; }
        }
        var fallback=new ClientWebSocket(); fallback.Options.Proxy=systemProxy;
        try { await fallback.ConnectAsync(uri,token); return fallback; }
        catch { fallback.Dispose(); throw; }
    }
    public static async Task<Connection> ConnectHost(string relayUrl,IdentityStore identity,string hostFingerprint,CancellationToken token)
    {
        if(!IsFingerprint(hostFingerprint)) throw new ArgumentException("Not a HearthLink device identity.",nameof(hostFingerprint));
        return await Tunnel(await Open(relayUrl,RelayProtocol.Connect+"?to="+hostFingerprint,identity,token),identity,hostFingerprint,null,token);
    }
    /// <summary>Reaches the host that opened pairing under <paramref name="code"/>'s lookup. The caller then sends the "pair" request with <see cref="PairingCode.Secret"/>.</summary>
    public static async Task<Connection> ConnectForPairing(string relayUrl,IdentityStore identity,PairingCode code,Func<string,bool> confirm,CancellationToken token)
    {
        if(code.Lookup is not {Length:4} lookup || !lookup.All(char.IsAsciiDigit)) throw new ArgumentException("The pairing code is not valid.",nameof(code));
        return await Tunnel(await Open(relayUrl,RelayProtocol.Connect+"?pair="+lookup,identity,token),identity,null,confirm,token);
    }
    /// <summary>Waits for the relay's "ready", then runs pinned (or confirmed) mutual TLS end to end with the host through the tunnel.</summary>
    static async Task<Connection> Tunnel(ClientWebSocket socket,IdentityStore identity,string? expected,Func<string,bool>? confirm,CancellationToken token)
    {
        try
        {
            using var wait=CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(ReadyLimit);
            string? ready;
            try { ready=await Receive(socket,new byte[4096],wait.Token,token); }
            catch(RelayException e) when(e.CloseCode==0 && wait.IsCancellationRequested && !token.IsCancellationRequested) { throw RelayException.For(RelayProtocol.Timeout); }
            using(var message=Parse(ready)) if(Type(message)!="ready") throw RelayException.For(RelayProtocol.BadMessage);
        }
        catch { socket.Dispose(); throw; }
        using var handshake=CancellationTokenSource.CreateLinkedTokenSource(token); handshake.CancelAfter(TimeSpan.FromSeconds(confirm==null?15:120));
        return await Connection.Secure(new RelayStream(socket),identity,expected,confirm,handshake.Token);
    }
    /// <summary>The subset of <paramref name="hosts"/> that are present on the relay and allow this PC.</summary>
    public static async Task<HashSet<string>> Online(string relayUrl,IdentityStore identity,IEnumerable<string> hosts,CancellationToken token)
    {
        var online=new HashSet<string>(StringComparer.Ordinal);
        foreach(var chunk in hosts.Where(IsFingerprint).Distinct(StringComparer.Ordinal).Chunk(64))
        {
            using var socket=await Open(relayUrl,RelayProtocol.Status,identity,token);
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(HandshakeLimit);
            await Send(socket,JsonSerializer.Serialize(new {t="query",ids=chunk}),limit.Token,token);
            using var reply=Parse(await Receive(socket,new byte[RelayProtocol.MessageBytes],limit.Token,token));
            if(Type(reply)!="status" || !reply.RootElement.TryGetProperty("online",out var ids) || ids.ValueKind!=JsonValueKind.Array) throw RelayException.For(RelayProtocol.BadMessage);
            foreach(var id in ids.EnumerateArray()) if(id.ValueKind==JsonValueKind.String && chunk.Contains(id.GetString())) online.Add(id.GetString()!);
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,null,limit.Token); } catch(Exception e) when(e is WebSocketException or OperationCanceledException or IOException) { }
        }
        return online;
    }
    /// <summary>Reads one text message. A close from the relay surfaces as its <see cref="RelayException"/>; a dropped connection or <paramref name="limit"/> expiring as code 0.</summary>
    internal static async Task<string> Receive(WebSocket socket,byte[] buffer,CancellationToken limit,CancellationToken caller)
    {
        int count=0;
        try
        {
            while(true)
            {
                if(count==buffer.Length) throw RelayException.For(RelayProtocol.BadMessage);
                var result=await socket.ReceiveAsync(buffer.AsMemory(count),limit);
                if(result.MessageType==WebSocketMessageType.Close) throw RelayException.For((int?)socket.CloseStatus ?? 0);
                if(result.MessageType!=WebSocketMessageType.Text) throw RelayException.For(RelayProtocol.BadMessage);
                count+=result.Count;
                if(result.EndOfMessage) return Encoding.UTF8.GetString(buffer,0,count);
            }
        }
        catch(Exception e) when(e is WebSocketException or IOException || e is OperationCanceledException && !caller.IsCancellationRequested) { throw RelayException.For(0); }
    }
    internal static async Task Send(WebSocket socket,string text,CancellationToken limit,CancellationToken caller)
    {
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(text),WebSocketMessageType.Text,true,limit); }
        catch(Exception e) when(e is WebSocketException or IOException || e is OperationCanceledException && !caller.IsCancellationRequested) { throw RelayException.For(0); }
    }
    internal static JsonDocument Parse(string text) { try { return JsonDocument.Parse(text); } catch(JsonException) { throw RelayException.For(RelayProtocol.BadMessage); } }
    internal static string? Type(JsonDocument message)=>message.RootElement.ValueKind==JsonValueKind.Object && message.RootElement.TryGetProperty("t",out var t) && t.ValueKind==JsonValueKind.String ? t.GetString() : null;
    internal static string? Text(JsonDocument message,string name)=>message.RootElement.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.String ? value.GetString() : null;
}
/// <summary>
/// Keeps this host present on the relay: authenticates, publishes the allow list, pings every 20 s and hands each incoming
/// controller to <c>incoming</c> as a relay tunnel. Never throws out of its loop; reconnects with 1, 2, 4 … 60 s back-off.
/// </summary>
public sealed class RelayPresence(IdentityStore identity,Func<IReadOnlyList<string>> allowed,Func<Stream,string,Task> incoming) : IDisposable
{
    public const string Online="Online through relay", Connecting="Connecting to relay…", Unreachable="Relay unreachable • LAN only";
    static readonly TimeSpan PingEvery=TimeSpan.FromSeconds(20), Silence=TimeSpan.FromSeconds(70), Steady=TimeSpan.FromMinutes(2);
    readonly CancellationTokenSource lifetime=new();
    readonly SemaphoreSlim sending=new(1,1), tunnels=new(8,8);
    readonly object sync=new();
    ClientWebSocket? socket;
    TaskCompletionSource<bool>? pairing;
    Task? loop;
    string state="";
    public string State { get { lock(sync) return state; } }
    public event Action<string>? StateChanged;
    public void Start() { lock(sync) loop??=Task.Run(Run); }
    /// <summary>Sends the current allow list; a no-op while offline because every reconnect publishes it.</summary>
    public async Task PublishAllowed() => await TrySend(AllowMessage());
    /// <summary>Claims a pairing lookup on the relay; true on "pairing-ok", false if taken, offline or unanswered within 10 s.</summary>
    public async Task<bool> OpenPairing(string lookup)
    {
        var answer=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(sync) { pairing?.TrySetResult(false); pairing=answer; }
        try
        {
            if(!await TrySend(JsonSerializer.Serialize(new {t="pairing",lookup}))) return false;
            return await answer.Task.WaitAsync(TimeSpan.FromSeconds(10),lifetime.Token);
        }
        catch(Exception e) when(e is TimeoutException or OperationCanceledException) { return false; }
        finally { lock(sync) if(pairing==answer) pairing=null; }
    }
    public async Task ClosePairing() => await TrySend("{\"t\":\"pairing-close\"}");
    public void Dispose()
    {
        lifetime.Cancel();
        lock(sync) { pairing?.TrySetResult(false); try { socket?.Abort(); } catch(ObjectDisposedException) { } }
    }
    string AllowMessage()
    {
        IReadOnlyList<string> ids;
        try { ids=allowed(); } catch(Exception) { ids=[]; }
        return JsonSerializer.Serialize(new {t="allow",ids=ids.Where(RelayClient.IsFingerprint).Distinct().ToArray()});
    }
    void Report(string next)
    {
        lock(sync) { if(state==next) return; state=next; }
        try { StateChanged?.Invoke(next); } catch(Exception) { /* a faulty subscriber must not stop presence */ }
    }
    async Task<bool> TrySend(string text)
    {
        ClientWebSocket? current; lock(sync) current=socket;
        if(current==null) return false;
        return await Send(current,text);
    }
    async Task<bool> Send(ClientWebSocket target,string text)
    {
        try
        {
            await sending.WaitAsync(lifetime.Token);
            try { await RelayClient.Send(target,text,lifetime.Token,lifetime.Token); return true; } finally { sending.Release(); }
        }
        catch(Exception e) when(e is RelayException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { return false; }
    }
    async Task Run()
    {
        int failures=0, replaced=0;
        while(!lifetime.IsCancellationRequested)
        {
            Report(Connecting);
            ClientWebSocket? current=null; int closed=-1; DateTime since=DateTime.UtcNow; bool connected=false;
            try
            {
                current=await RelayClient.Open(identity.RelayUrl,RelayProtocol.Presence+"?id="+identity.Fingerprint,identity,lifetime.Token);
                lock(sync) socket=current;
                if(!await Send(current,AllowMessage())) throw RelayException.For(0);
                connected=true; failures=0; since=DateTime.UtcNow; Report(Online);
                closed=await Pump(current);
            }
            catch(Exception e) when(e is not OutOfMemoryException) { /* every failure ends in a reconnect */ }
            finally
            {
                lock(sync) { socket=null; pairing?.TrySetResult(false); }
                current?.Dispose();
            }
            if(lifetime.IsCancellationRequested) break;
            TimeSpan delay;
            if(!connected) { Report(Unreachable); delay=Backoff(failures++); }
            else if(closed==RelayProtocol.Replaced) { replaced=DateTime.UtcNow-since>Steady?1:replaced+1; delay=Backoff(replaced); }
            else { if(DateTime.UtcNow-since>Steady) replaced=0; delay=Backoff(0); }
            try { await Task.Delay(delay,lifetime.Token); } catch(OperationCanceledException) { break; }
        }
    }
    static TimeSpan Backoff(int step)=>TimeSpan.FromSeconds(Math.Min(60,1<<Math.Min(step,6)));
    /// <summary>Serves one presence socket until it closes; returns the close code (0 for a dropped connection).</summary>
    async Task<int> Pump(ClientWebSocket current)
    {
        using var session=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var pinger=Task.Run(async()=>
        {
            try { while(true) { await Task.Delay(PingEvery,session.Token); if(!await Send(current,"ping")) break; } }
            catch(OperationCanceledException) { }
        });
        var buffer=new byte[RelayProtocol.MessageBytes];
        try
        {
            while(true)
            {
                string text;
                // Pongs answer every ping, so a long silence means the connection is dead even if TCP has not noticed.
                using(var quiet=CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                {
                    quiet.CancelAfter(Silence);
                    try { text=await RelayClient.Receive(current,buffer,quiet.Token,lifetime.Token); }
                    catch(RelayException e) { return e.CloseCode; }
                }
                if(text=="pong") continue;
                JsonDocument message;
                try { message=JsonDocument.Parse(text); } catch(JsonException) { continue; }
                using(message) Handle(message);
            }
        }
        finally { session.Cancel(); await pinger; }
    }
    void Handle(JsonDocument message)
    {
        switch(RelayClient.Type(message))
        {
            case "pairing-ok": lock(sync) pairing?.TrySetResult(true); break;
            case "pairing-taken": lock(sync) pairing?.TrySetResult(false); break;
            case "incoming":
                string? session=RelayClient.Text(message,"session"), from=RelayClient.Text(message,"from");
                if(session is not {Length:32} || !session.All(c=>char.IsAsciiDigit(c) || c is >='a' and <='f') || !RelayClient.IsFingerprint(from)) break;
                if(!tunnels.Wait(0)) break;
                _=Task.Run(()=>Accept(session,from!));
                break;
        }
    }
    async Task Accept(string session,string from)
    {
        try
        {
            var tunnel=await RelayClient.Open(identity.RelayUrl,$"{RelayProtocol.Accept}?host={identity.Fingerprint}&session={session}",identity,lifetime.Token);
            using var stream=new RelayStream(tunnel);
            await incoming(stream,from);
        }
        catch(Exception e) when(e is not OutOfMemoryException) { /* a failed or faulty session never reaches the presence loop */ }
        finally { tunnels.Release(); }
    }
}
