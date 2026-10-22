using HearthLink.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static class UpdateChecks
{
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(respond(request));
    }
    public static async Task Run(Action<bool,string> check)
    {
        byte[] installer=Encoding.UTF8.GetBytes("fixture bytes only; this is not an executable");
        string hash=Convert.ToHexString(SHA256.HashData(installer));
        string installerUrl="https://github.com/pitchiluxe/hearthlink/releases/download/v0.2.1/HearthLink-0.2.1-win-x64-setup.exe";
        string sumsUrl="https://github.com/pitchiluxe/hearthlink/releases/download/v0.2.1/SHA256SUMS.txt";
        string metadata=JsonSerializer.Serialize(new {tag_name="v0.2.1",draft=false,prerelease=false,assets=new[]{new {name="HearthLink-0.2.1-win-x64-setup.exe",browser_download_url=installerUrl,size=installer.Length},new {name="SHA256SUMS.txt",browser_download_url=sumsUrl,size=100}}});
        bool corrupt=false,redirect=false;
        using var client=new HttpClient(new Handler(request=>
        {
            if(request.RequestUri!.Host=="api.github.com") return new(HttpStatusCode.OK) {Content=new StringContent(metadata)};
            if(request.RequestUri.AbsoluteUri==sumsUrl) return new(HttpStatusCode.OK) {Content=new StringContent($"{hash}  HearthLink-0.2.1-win-x64-setup.exe\n")};
            if(redirect) {var response=new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location=new("http://github.com/unsafe.exe"); return response;}
            return new(HttpStatusCode.OK) {Content=new ByteArrayContent(corrupt?new byte[installer.Length]:installer)};
        }));
        var updates=new AppUpdates(client);
        var release=await updates.CheckAsync(new(0,2,0));
        check(release?.Version==new Version(0,2,1) && release.Sha256==hash,"update check pairs exact installer with its published checksum");
        check(await updates.CheckAsync(new(0,2,1))==null && await updates.CheckAsync(new(0,3,0))==null,"update check does not reinstall or downgrade");
        string folder=Path.Combine(Path.GetTempPath(),"HearthLink-updater-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            string path=Path.Combine(folder,"fixture.download");
            await updates.DownloadAsync(release!,path);
            check(File.ReadAllBytes(path).SequenceEqual(installer),"download bytes are verified and preserved");
            bool rejected=false; try {await updates.DownloadAsync(release!,path);} catch(IOException) {rejected=true;}
            check(rejected && File.ReadAllBytes(path).SequenceEqual(installer),"update download refuses to overwrite an existing file");
            File.Delete(path); corrupt=true; rejected=false;
            try {await updates.DownloadAsync(release!,path);} catch(InvalidDataException) {rejected=true;}
            check(rejected && !File.Exists(path),"corrupt installer is rejected and partial file removed");
            corrupt=false; redirect=true; rejected=false;
            try {await updates.DownloadAsync(release!,path);} catch(InvalidDataException) {rejected=true;}
            check(rejected && !File.Exists(path),"update rejects HTTPS downgrade redirects");
            redirect=false; using var cancelled=new CancellationTokenSource(); cancelled.Cancel(); rejected=false;
            try {await updates.DownloadAsync(release!,path,token:cancelled.Token);} catch(OperationCanceledException) {rejected=true;}
            check(rejected && !File.Exists(path),"cancelled update leaves no installer to execute");
            metadata=metadata.Replace(installerUrl,"https://example.com/malware.exe"); rejected=false;
            try {await updates.CheckAsync(new(0,2,0));} catch(InvalidDataException) {rejected=true;}
            check(rejected,"release cannot redirect installer metadata to a different publisher");
        }
        finally {Directory.Delete(folder,true);}
    }
}
