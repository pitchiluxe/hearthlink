using HearthLink.Core;
using System.Net;
using System.Text.Json.Nodes;

static class ModelScanChecks
{
    sealed class Handler(bool slow=false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.AbsolutePath=="/api/tags") return new(HttpStatusCode.OK) {Content=new StringContent("""{"models":[{"name":"good:latest","size":1000},{"name":"broken:latest","size":1000},{"name":"other:latest","size":2000}]}""")};
            string name=JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!["model"]!.GetValue<string>();
            if(slow && name=="broken:latest") await Task.Delay(Timeout.Infinite,token);
            return name=="broken:latest"?new(HttpStatusCode.InternalServerError) {Content=new StringContent("""{"error":"read GGUF metadata: model blob does not exist"}""")}:
                new(HttpStatusCode.OK) {Content=new StringContent("""{"capabilities":["completion","tools"]}""")};
        }
    }
    public static async Task Run(Action<bool,string> check)
    {
        using var http=new HttpClient(new Handler());
        bool failed=false; IReadOnlyList<AiModel> found=[];
        try {found=await new Ollama(http).Models(CancellationToken.None);} catch(AiUnavailableException) {failed=true;}
        check(!failed && found.Select(m=>m.Name).SequenceEqual(["good:latest","other:latest"]),"one missing Ollama model blob does not discard other verified installed models");
        var result=await new Ollama(http).ScanModels(CancellationToken.None);
        check(result.Issues.Count==1 && result.Issues[0].Model=="broken:latest" && result.Issues[0].Reason.Contains("repair") && result.Checked==3,"scan identifies the broken model and returns repair guidance without exposing its blob path");
        using var slowHttp=new HttpClient(new Handler(true));
        var clock=System.Diagnostics.Stopwatch.StartNew();
        var timed=await new Ollama(slowHttp).ScanModels(CancellationToken.None);
        check(timed.Models.Count==2 && timed.Issues.Any(i=>i.Model=="broken:latest" && i.Reason.Contains("timed out")) && clock.Elapsed<TimeSpan.FromSeconds(8),"a stalled metadata check times out independently without losing healthy models");
        using var stop=new CancellationTokenSource(); stop.Cancel();
        bool cancelled=false; try {await new Ollama(http).ScanModels(stop.Token);} catch(OperationCanceledException) {cancelled=true;}
        check(cancelled,"explicit scan cancellation is propagated rather than silently starting another operation");
    }
}
