using System.Security.Cryptography;
using System.Text;
namespace HearthLink.Core;
/// <summary>One-time pairing code. Wrong guesses lock out only the guessing peer, so one LAN device cannot close the window for everyone; a global cap still bounds brute force.</summary>
public sealed class PairingGate
{
    public const int PeerAttempts=5, TotalAttempts=50;
    readonly Dictionary<string,int> attempts=new(StringComparer.Ordinal);
    string? current;
    DateTimeOffset expires;
    int total;
    public string Open(DateTimeOffset now)
    {
        current=RandomNumberGenerator.GetInt32(0,100000000).ToString("D8");
        expires=now.AddMinutes(2); attempts.Clear(); total=0; return current;
    }
    public bool Consume(string code, DateTimeOffset now, string peer="")
    {
        if(current==null || now>=expires || total>=TotalAttempts) return false;
        attempts.TryGetValue(peer,out int used);
        if(used>=PeerAttempts) return false;
        attempts[peer]=used+1; total++;
        if(!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code),Encoding.UTF8.GetBytes(current))) return false;
        current=null; return true;
    }
}
/// <summary>Display names supplied by a remote peer. Strips control, format (bidi override, zero-width) and line/paragraph separator characters.</summary>
public static class PeerName
{
    public const int MaxLength=64;
    public static string Clean(string? name)
    {
        var kept=new string((name??"").Where(c=>!char.IsControl(c) && char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)).ToArray()).Trim();
        if(kept.Length>MaxLength) kept=kept[..(char.IsHighSurrogate(kept[MaxLength-1])?MaxLength-1:MaxLength)].TrimEnd();
        return kept;
    }
    public static bool Valid(string? name)=>!string.IsNullOrEmpty(name) && Clean(name)==name;
}
public sealed class TrustBook
{
    readonly object sync=new();
    public Dictionary<string,string> Entries { get; set; } = new(StringComparer.Ordinal);
    public bool Contains(string fingerprint) { lock(sync) return Entries.ContainsKey(fingerprint); }
    public void Add(string fingerprint,string name) { lock(sync) Entries[fingerprint]=name; }
    public void Remove(string fingerprint) { lock(sync) Entries.Remove(fingerprint); }
}
