using HearthLink.Core;
using System.Net.Security;
using System.Net.Sockets;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace HearthLink;
public sealed class Connection : IDisposable
{
    /// <summary>TCP port for direct connections. Always 45831 in the app; the Windows checks move it to a free port so they can run beside an installed HearthLink.</summary>
    public static int Port { get; set; }=45831;
    public readonly SslStream Stream;
    readonly Stream transport;
    readonly SemaphoreSlim write=new(1,1);
    public Connection(Stream transport,SslStream stream) { this.transport=transport; Stream=stream; }
    public async Task Send(Packet packet,CancellationToken token=default)
    {
        await write.WaitAsync(token);
        try { await Wire.Write(Stream,packet,token); } finally { write.Release(); }
    }
    public Task<Packet> Read(CancellationToken token=default)=>Wire.Read(Stream,token);
    public string RemoteFingerprint=>IdentityStore.FingerprintOf(Stream.RemoteCertificate ?? throw new InvalidOperationException("No remote certificate."));
    /// <summary>Status probe. An "online" answer means the host still trusts this PC, so a one-way pairing completes here.</summary>
    public async Task<Packet> Probe(IdentityStore identity,CancellationToken token)
    {
        await Send(new("probe"),token);
        var reply=await Read(token);
        if(reply.Kind=="online") identity.UpgradeTrust(RemoteFingerprint,null);
        return reply;
    }
    /// <summary>Sends a pairing request; on "paired" trusts the host both ways and returns its name, otherwise null.</summary>
    public async Task<string?> Pair(IdentityStore identity,string code,string fallbackName,string? lanAddress,CancellationToken token)
    {
        await Send(new("pair",Name:PeerName.Clean(identity.Config.Name) is {Length:>0} own?own:"Controller",Code:code),token);
        var reply=await Read(token);
        if(reply.Kind!="paired") return null;
        string name=PeerName.Clean(reply.Name) is {Length:>0} n?n:fallbackName;
        identity.TrustBothWays(RemoteFingerprint,name,lanAddress); identity.Audit(RemoteFingerprint,"paired");
        return name;
    }
    public void Dispose() { Stream.Dispose(); transport.Dispose(); }
    public static async Task<Connection> Connect(string address,IdentityStore identity,string? expected,Func<string,bool>? confirm=null)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(confirm==null?15:120));
        var tcp=new TcpClient(AddressFamily.InterNetwork) { NoDelay=true };
        try
        {
            using(var connecting=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                IPAddress[] endpoints=[];
                try
                {
                    endpoints=(await Dns.GetHostAddressesAsync(address,connecting.Token)).Where(ip=>ip.AddressFamily==AddressFamily.InterNetwork).ToArray();
                    if(endpoints.Length==0) throw new SocketException((int)SocketError.HostNotFound);
                    await tcp.ConnectAsync(endpoints,Port,connecting.Token);
                }
                catch(OperationCanceledException e)
                {
                    throw new IOException($"Could not reach {address} on TCP {Port} within 5 seconds. On the OTHER PC, open HearthLink and enable incoming sessions. Check its active Ethernet/Wi-Fi IPv4 address, Private network profile and HearthLink firewall rule. A name already entered in the box cannot bypass a blocked network port.",e);
                }
                catch(SocketException e)
                {
                    string resolved=endpoints.Length==0?"No IPv4 address could be resolved.":"Windows resolved: "+string.Join(", ",endpoints.Select(ip=>ip.ToString()))+".";
                    throw new IOException($"Cannot connect to {address} on TCP {Port}. {resolved} Network result: {e.SocketErrorCode}. Check the OTHER PC's HearthLink host and its firewall/network profile, or enter its active LAN IPv4 address.",e);
                }
            }
            return await Secure(new NetworkStream(tcp.Client,true),identity,expected,confirm,timeout.Token);
        }
        catch { tcp.Dispose(); throw; }
    }
    public static async Task<Connection> Secure(Stream transport,IdentityStore identity,string? expected,Func<string,bool>? confirm,CancellationToken token)
    {
        var ssl=new SslStream(transport,false,(_,cert,_,_)=>
        {
            if(cert==null) return false;
            string actual=IdentityStore.FingerprintOf(cert);
            return expected!=null ? actual==expected : confirm?.Invoke(actual)==true;
        });
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost="HearthLink", EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
                ClientCertificates=new X509CertificateCollection {identity.Certificate}, CertificateRevocationCheckMode=X509RevocationMode.NoCheck
            },token);
            return new(transport,ssl);
        }
        catch { ssl.Dispose(); transport.Dispose(); throw; }
    }
}
