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
    public bool DefaultControl {get;set;}=true;
    public int DefaultQuality {get;set;}
    public List<string> Favorites {get;set;}=[];
    public TrustBook Controllers { get; set; }=new();
    public List<Peer> Hosts { get; set; }=[];
    /// <summary>A relay the user chose; empty means the app's built-in <see cref="RelayClient.DefaultUrl"/>, so saved settings follow app updates.</summary>
    public string RelayUrl { get; set; }="";
    public bool LanListener { get; set; }
    /// <summary>Paired controllers start sessions without the Allow dialog. Off by default; only <see cref="MainWindow.ChangeUnattended"/> changes it, after Windows Hello.</summary>
    public bool Unattended { get; set; }
}
public sealed class IdentityStore
{
    readonly string root;
    public Settings Config { get; }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint => FingerprintOf(Certificate);
    /// <summary>A relay address for this run only (never saved), e.g. from HEARTHLINK_RELAY_URL.</summary>
    public string? RelayOverride { get; set; }
    public string RelayUrl => !string.IsNullOrWhiteSpace(RelayOverride) ? RelayOverride.Trim() : Chosen(Config.RelayUrl) ?? RelayClient.DefaultUrl;
    // Pre-release 0.3.0 builds saved their built-in address into settings, including the undeployed placeholder.
    static readonly string[] SavedDefaults=["wss://hearthlink-relay.REPLACE.workers.dev",RelayClient.DefaultUrl];
    static string? Chosen(string saved)=>string.IsNullOrWhiteSpace(saved) || SavedDefaults.Contains(saved.Trim()) ? null : saved.Trim();
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
    /// <summary>Trusts <paramref name="fingerprint"/> as both controller and host. Existing entries keep their name and address; only an empty address is filled. Saves and returns true when anything changed.</summary>
    public bool TrustBothWays(string fingerprint,string name,string? lanAddress)
    {
        string clean=PeerName.Clean(name) is {Length:>0} n?n:"Paired PC"; bool changed=false;
        if(!Config.Controllers.Contains(fingerprint)) { Config.Controllers.Add(fingerprint,clean); changed=true; }
        int index=Config.Hosts.FindIndex(p=>p.Fingerprint==fingerprint);
        if(index<0) { Config.Hosts.Add(new(clean,lanAddress??"",fingerprint)); changed=true; }
        else if(Config.Hosts[index].Address.Length==0 && !string.IsNullOrEmpty(lanAddress)) { Config.Hosts[index]=Config.Hosts[index] with {Address=lanAddress}; changed=true; }
        if(changed) Save();
        return changed;
    }
    /// <summary>Completes a one-way 0.2.x pairing after a pinned exchange. Acts only for a PC already trusted in one role, so it never trusts an unpaired PC.</summary>
    public bool UpgradeTrust(string fingerprint,string? lanAddress)
    {
        string? name=Config.Controllers.Entries.GetValueOrDefault(fingerprint) ?? Config.Hosts.Find(p=>p.Fingerprint==fingerprint)?.Name;
        if(name==null || !TrustBothWays(fingerprint,name,lanAddress)) return false;
        Audit(fingerprint,"trust-upgraded"); return true;
    }
    /// <summary>Removes a PC in both roles. Saves and returns true when it was trusted in either.</summary>
    public bool Remove(string fingerprint)
    {
        bool had=Config.Controllers.Contains(fingerprint);
        Config.Controllers.Remove(fingerprint);
        had|=Config.Hosts.RemoveAll(p=>p.Fingerprint==fingerprint)>0;
        if(had) Save();
        return had;
    }
    public void Audit(string fingerprint,string outcome)
    {
        // Metadata only; no names supplied by remote callers, content, code or key material.
        File.AppendAllText(Path.Combine(root,"history.jsonl"),JsonSerializer.Serialize(new {at=DateTimeOffset.UtcNow,peer=fingerprint,outcome})+Environment.NewLine);
    }
    /// <summary>At most the last 512 KiB/1000 valid metadata records, newest first. No identity or content is exported.</summary>
    public IReadOnlyList<HistoryEntry> ReadHistory()
    {
        string path=Path.Combine(root,"history.jsonl");
        if(!File.Exists(path)) return [];
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        long length=stream.Length; bool tail=length>512*1024;
        long start=Math.Max(0,length-512*1024); stream.Seek(start,SeekOrigin.Begin);
        byte[] bytes=new byte[(int)(length-start)]; int read=0;
        while(read<bytes.Length) {int count=stream.Read(bytes,read,bytes.Length-read); if(count==0) break; read+=count;}
        using var snapshot=new MemoryStream(bytes,0,read);
        using var reader=new StreamReader(snapshot);
        if(tail) reader.ReadLine();
        var entries=new Queue<HistoryEntry>();
        while(reader.ReadLine() is {} line)
        {
            if(HistoryEntry.Parse(line) is not {} entry) continue;
            entries.Enqueue(entry); if(entries.Count>1000) entries.Dequeue();
        }
        return entries.Reverse().ToArray();
    }
}
