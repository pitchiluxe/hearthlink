using System.Security.Cryptography;
using System.Text;
namespace HearthLink.Core;
public sealed class PairingGate
{
    string? current;
    DateTimeOffset expires;
    int attempts;
    public string Open(DateTimeOffset now)
    {
        current=RandomNumberGenerator.GetInt32(0,100000000).ToString("D8");
        expires=now.AddMinutes(2); attempts=0; return current;
    }
    public bool Consume(string code, DateTimeOffset now)
    {
        if(current==null || now>=expires || attempts>=5) return false;
        attempts++;
        if(!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code),Encoding.UTF8.GetBytes(current))) return false;
        current=null; return true;
    }
}
public sealed class TrustBook
{
    readonly object sync=new();
    public Dictionary<string,string> Entries { get; set; } = new(StringComparer.Ordinal);
    public bool Contains(string fingerprint) { lock(sync) return Entries.ContainsKey(fingerprint); }
    public void Add(string fingerprint,string name) { lock(sync) Entries[fingerprint]=name; }
    public void Remove(string fingerprint) { lock(sync) Entries.Remove(fingerprint); }
}
