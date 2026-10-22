using System.Security.Cryptography;
using System.Text;
namespace HearthLink.Core;
public static class RelayProtocol
{
    public const string Presence="/v1/presence", Status="/v1/status", Connect="/v1/connect", Accept="/v1/accept";
    public const int MessageBytes=65536;
    public const int BadMessage=4400, NotAuthenticated=4401, Forbidden=4403, Offline=4404, Timeout=4408, Replaced=4409;
    public static byte[] SignatureInput(string endpoint, ReadOnlySpan<byte> nonce)
    {
        byte[] prefix=Encoding.UTF8.GetBytes("hearthlink-relay-v1\n"+endpoint+"\n"), result=new byte[prefix.Length+nonce.Length];
        prefix.CopyTo(result,0); nonce.CopyTo(result.AsSpan(prefix.Length)); return result;
    }
}
public readonly record struct PairingCode(string Lookup, string Secret)
{
    public static PairingCode New(string secret) => new(RandomNumberGenerator.GetInt32(10000).ToString("D4"),secret);
    public string Display => $"{Lookup}-{Secret[..4]}-{Secret[4..]}";
    public static bool TryParse(string text, out PairingCode code)
    {
        code=default; string digits=text.Replace(" ","").Replace("-","");
        if(digits.Length!=12 || !digits.All(char.IsAsciiDigit)) return false;
        code=new(digits[..4],digits[4..]); return true;
    }
}
