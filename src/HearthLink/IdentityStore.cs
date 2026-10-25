using HearthLink.Core;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace HearthLink;
public sealed record Peer(string Name,string Address,string Fingerprint,DateTimeOffset? LastSeen=null);
public sealed class Settings
{
    public string Name { get; set; }=Environment.MachineName;
    public string LastTarget {get;set;}="";
    public bool CheckUpdates {get;set;}=true;
    public bool Host { get; set; }
    public bool Controller { get; set; }=true;
    public TrustBook Controllers { get; set; }=new();
    public List<Peer> Hosts { get; set; }=[];
    public string RelayUrl { get; set; }=RelayClient.DefaultUrl;
}
public sealed class IdentityStore
{
    readonly string root;
    public Settings Config { get; }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint => FingerprintOf(Certificate);
    public static string FingerprintOf(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
    public IdentityStore(string? folder=null)
    {
        root=folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HearthLink");
        Directory.CreateDirectory(root);
        string config=Path.Combine(root,"settings.protected");
        Config=File.Exists(config) ? JsonSerializer.Deserialize<Settings>(Unprotect(File.ReadAllBytes(config))) ?? throw new InvalidDataException("Invalid settings") : new();
        string key=Path.Combine(root,"identity.protected");
        if(File.Exists(key)) Certificate=X509CertificateLoader.LoadPkcs12(Unprotect(File.ReadAllBytes(key)),null,X509KeyStorageFlags.UserKeySet);
        else
        {
            using var rsa=RSA.Create(3072);
            var request=new CertificateRequest("CN=HearthLink",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,false));
            using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(5));
            byte[] pfx=cert.Export(X509ContentType.Pfx);
            File.WriteAllBytes(key,Protect(pfx));
            // Schannel needs a user-key container. Windows protects the imported key;
            // without PersistKeySet the container is cleaned up with the certificate.
            Certificate=X509CertificateLoader.LoadPkcs12(pfx,null,X509KeyStorageFlags.UserKeySet);
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
    static byte[] Protect(byte[] bytes)=>ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser);
    static byte[] Unprotect(byte[] bytes)=>ProtectedData.Unprotect(bytes,null,DataProtectionScope.CurrentUser);
    public void Save()
    {
        string target=Path.Combine(root,"settings.protected");
        File.WriteAllBytes(target+".new",Protect(JsonSerializer.SerializeToUtf8Bytes(Config)));
        File.Move(target+".new",target,true);
    }
    public void Audit(string fingerprint,string outcome)
    {
        // Metadata only; no names supplied by remote callers, content, code or key material.
        File.AppendAllText(Path.Combine(root,"history.jsonl"),JsonSerializer.Serialize(new {at=DateTimeOffset.UtcNow,peer=fingerprint,outcome})+Environment.NewLine);
    }
}
