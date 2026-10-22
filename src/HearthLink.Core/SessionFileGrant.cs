using System.Security.Cryptography;
namespace HearthLink.Core;
public sealed class SessionFileGrant
{
    readonly string fingerprint;
    readonly byte[] token=RandomNumberGenerator.GetBytes(32);
    readonly bool control;
    readonly CancellationToken lifetime;
    public string Token=>Convert.ToHexString(token);
    public CancellationToken Lifetime=>lifetime;
    public SessionFileGrant(string fingerprint,bool control,CancellationToken lifetime) {this.fingerprint=fingerprint; this.control=control; this.lifetime=lifetime;}
    public bool Allows(string peer,string supplied)
    {
        if(!control || lifetime.IsCancellationRequested || peer!=fingerprint || supplied.Length!=64) return false;
        try {return CryptographicOperations.FixedTimeEquals(token,Convert.FromHexString(supplied));} catch(FormatException) {return false;}
    }
}
