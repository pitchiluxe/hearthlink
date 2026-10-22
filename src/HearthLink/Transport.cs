using HearthLink.Core;
using System.Net.Security;
using System.Net.Sockets;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace HearthLink;
public sealed class Connection : IDisposable
{
    public const int Port=45831;
    public readonly TcpClient Tcp;
    public readonly SslStream Stream;
    readonly SemaphoreSlim write=new(1,1);
    public Connection(TcpClient tcp,SslStream stream) { Tcp=tcp; Stream=stream; }
    public async Task Send(Packet packet,CancellationToken token=default)
    {
        await write.WaitAsync(token);
        try { await Wire.Write(Stream,packet,token); } finally { write.Release(); }
    }
    public Task<Packet> Read(CancellationToken token=default)=>Wire.Read(Stream,token);
    public void Dispose() { Stream.Dispose(); Tcp.Dispose(); }
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
            var ssl=new SslStream(tcp.GetStream(),false,(_,cert,_,_)=>
            {
                if(cert==null) return false;
                string actual=IdentityStore.FingerprintOf(cert);
                return expected!=null ? actual==expected : confirm?.Invoke(actual)==true;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost="HearthLink", EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
                ClientCertificates=new X509CertificateCollection {identity.Certificate}, CertificateRevocationCheckMode=X509RevocationMode.NoCheck
            },timeout.Token);
            return new(tcp,ssl);
        }
        catch { tcp.Dispose(); throw; }
    }
}
