using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace HearthLink.Core;

public sealed record AppRelease(Version Version, Uri Download, string Sha256, long Size);

public sealed class AppUpdates
{
    public const string Repository="pitchiluxe/hearthlink";
    public const string Website="https://hearthlink.vercel.app/";
    public const long MaxInstallerBytes=300L*1024*1024;
    readonly HttpClient client;
    public AppUpdates(HttpClient client) {this.client=client;}
    public static HttpClient CreateClient()=>new(new HttpClientHandler {AllowAutoRedirect=false}) {Timeout=Timeout.InfiniteTimeSpan};

    static bool Allowed(Uri uri)=>uri.Scheme=="https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host=="api.github.com" || uri.Host=="github.com" || uri.Host=="release-assets.githubusercontent.com" || uri.Host=="objects.githubusercontent.com");

    async Task<HttpResponseMessage> Get(Uri uri,CancellationToken token)
    {
        for(int i=0;i<6;i++)
        {
            if(!Allowed(uri)) throw new InvalidDataException("Update address is outside the trusted GitHub download hosts.");
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            request.Headers.UserAgent.ParseAdd("HearthLink-Updater/1.0");
            var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
            if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location=response.Headers.Location; response.Dispose();
                if(location==null) throw new InvalidDataException("Missing update redirect address.");
                uri=new Uri(uri,location); continue;
            }
            return response;
        }
        throw new InvalidDataException("Too many update redirects.");
    }

    async Task<byte[]> SmallBody(Uri uri,CancellationToken token)
    {
        using var response=await Get(uri,token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        await using var input=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output=new MemoryStream(); byte[] buffer=new byte[8192]; int n;
        while((n=await input.ReadAsync(buffer,token).ConfigureAwait(false))>0)
        {if(output.Length+n>1024*1024) throw new InvalidDataException("Update metadata is too large."); await output.WriteAsync(buffer.AsMemory(0,n),token).ConfigureAwait(false);}
        return output.ToArray();
    }

    public async Task<AppRelease?> CheckAsync(Version installed,CancellationToken token=default)
    {
        byte[] body=await SmallBody(new($"https://api.github.com/repos/{Repository}/releases/latest"),token).ConfigureAwait(false);
        using var doc=JsonDocument.Parse(body); var release=doc.RootElement;
        if(release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag=release.GetProperty("tag_name").GetString()??"";
        if(!Version.TryParse(tag.TrimStart('v'),out var version) || version<=installed) return null;
        string filename=$"HearthLink-{version}-win-x64-setup.exe";
        Uri? download=null,checksums=null; long size=0;
        foreach(var asset in release.GetProperty("assets").EnumerateArray())
        {
            string? name=asset.GetProperty("name").GetString();
            if(name!=filename && name!="SHA256SUMS.txt") continue;
            if(!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(),UriKind.Absolute,out var url)) throw new InvalidDataException("Invalid update asset address.");
            string expected=$"https://github.com/{Repository}/releases/download/{tag}/";
            if(!url.AbsoluteUri.StartsWith(expected,StringComparison.Ordinal) || !Allowed(url)) throw new InvalidDataException("Unexpected update asset address.");
            if(name==filename) {download=url; size=asset.GetProperty("size").GetInt64();} else checksums=url;
        }
        if(download==null || checksums==null || size<=0 || size>MaxInstallerBytes) throw new InvalidDataException("The release is missing a valid Windows installer or checksum file.");
        string sums=System.Text.Encoding.UTF8.GetString(await SmallBody(checksums,token).ConfigureAwait(false));
        string? hash=null;
        foreach(string line in sums.Split('\n'))
        {
            string[] parts=line.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length==2 && parts[1].TrimStart('*')==filename && parts[0].Length==64 && parts[0].All(Uri.IsHexDigit))
            {if(hash!=null) throw new InvalidDataException("Duplicate installer checksum."); hash=parts[0];}
        }
        if(hash==null) throw new InvalidDataException("Installer checksum is missing.");
        return new(version,download,hash,size);
    }

    public async Task DownloadAsync(AppRelease release,string destination,IProgress<int>? progress=null,CancellationToken token=default)
    {
        if(release.Size<=0 || release.Size>MaxInstallerBytes || !Allowed(release.Download) || release.Sha256.Length!=64 || !release.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid update download.");
        bool created=false,complete=false;
        try
        {
            using var response=await Get(release.Download,token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is long length && length!=release.Size) throw new InvalidDataException("Installer size does not match the release.");
            await using var input=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true); created=true;
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer=new byte[65536]; long total=0; int n;
            while((n=await input.ReadAsync(buffer,token).ConfigureAwait(false))>0)
            {
                total+=n; if(total>release.Size) throw new InvalidDataException("Installer exceeds its published size.");
                hash.AppendData(buffer,0,n); await output.WriteAsync(buffer.AsMemory(0,n),token).ConfigureAwait(false);
                progress?.Report((int)(total*99/release.Size));
            }
            if(total!=release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),Convert.FromHexString(release.Sha256))) throw new InvalidDataException("Installer verification failed. Nothing was installed.");
            await output.FlushAsync(token).ConfigureAwait(false); complete=true; progress?.Report(100);
        }
        finally {if(created && !complete) {try {File.Delete(destination);} catch(IOException) {} catch(UnauthorizedAccessException) {}}}
    }
}
