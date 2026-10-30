using HearthLink.Core;
using System.Text.Json.Nodes;
using System.Net;
using System.Text.Json;

static class AiChecks
{
    sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>respond(request);}
    sealed class Provider(string name,Func<CancellationToken,Task<string>> run) : IAiProvider
    {
        public int Calls;
        public async Task<AiReply> Chat(string model,string key,IReadOnlyList<AiMessage> messages,JsonNode? format,CancellationToken token)
        {Calls++; return new(await run(token),name,model);}
    }
    public static async Task Run(Action<bool,string> check)
    {
        var options=new AiOptions {OpenAIKey="fixture-only",OpenAIModel="fixture",ClaudeKey="fixture-only",ClaudeModel="fixture"};
        var local=new Provider("Ollama",_=>throw new AiUnavailableException("Ollama unavailable"));
        var openai=new Provider("OpenAI",_=>Task.FromResult("ok"));
        var claude=new Provider("Claude",_=>Task.FromResult("claude"));
        var router=new AiRouter(()=>options,local,openai,claude);
        bool unavailable=false; try {await router.Chat([new("user","hi")],null,CancellationToken.None);} catch(AiUnavailableException) {unavailable=true;}
        check(unavailable && openai.Calls==0 && claude.Calls==0,"cloud fallback remains off even when API keys exist");
        options=options with {CloudFallback=true};
        var reply=await router.Chat([new("user","hi")],null,CancellationToken.None);
        check(reply.Provider=="OpenAI" && local.Calls==2 && openai.Calls==1 && claude.Calls==0,"Ollama is attempted before OpenAI fallback");
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed=new Provider("Ollama",async _=> {started.SetResult(); await gate.Task; throw new AiUnavailableException("offline");});
        router=new(()=>options,delayed,openai,claude);
        var pending=router.Chat([new("user","hi")],null,CancellationToken.None); await started.Task;
        options=options with {CloudFallback=false}; gate.SetResult();
        try {await pending;} catch(AiUnavailableException) { }
        check(openai.Calls==1,"disabling cloud consent during the local attempt prevents cloud fallback");
        options=options with {CloudFallback=true};
        using var stop=new CancellationTokenSource(); stop.Cancel();
        bool cancelled=false; try {await router.Chat([],null,stop.Token);} catch(OperationCanceledException) {cancelled=true;}
        check(cancelled && openai.Calls==1,"cancelled requests never advance to a cloud provider");
        var invalid=new Provider("Ollama",_=>throw new InvalidDataException("malformed"));
        router=new(()=>options,invalid,openai,claude);
        bool malformed=false; try {await router.Chat([],null,CancellationToken.None);} catch(InvalidDataException) {malformed=true;}
        check(malformed && openai.Calls==1,"invalid local replies do not leak the request through fallback");
        var busy=new Provider("OpenAI",_=>throw new AiUnavailableException("busy"));
        router=new(()=>options,local,busy,claude);
        check((await router.Chat([],null,CancellationToken.None)).Provider=="Claude" && busy.Calls==1 && claude.Calls==1,"Claude follows an unavailable OpenAI provider");

        var policy=new PerformancePolicy(); var now=DateTimeOffset.UtcNow;
        var sample=new PerformanceSample(24,250,120,2,10);
        check(!policy.Observe(sample,now) && !policy.Observe(sample,now.AddSeconds(5)) && policy.Observe(sample,now.AddSeconds(10)),"agent requires three fresh congested samples");
        check(PerformancePolicy.ParseProfile("{\"profile\":0}",2,0,2)==1 && PerformancePolicy.ParseProfile("{\"profile\":4}",2,0,2)==2,"agent limits model recommendations to the configured range and one step");
        check(PerformancePolicy.ParseProfile("{\"profile\":99}",2,0,2)==null && PerformancePolicy.ParseProfile("run a shell command",2,0,2)==null,"agent rejects invalid or unstructured actions");
        policy=new();
        check(!policy.Observe(new(0,0,0,2,0),now) && !policy.Observe(new(1,10,4,2,1),now.AddSeconds(5)),"idle/static-screen low FPS alone never triggers quality changes");
        policy=new();
        check(!policy.Observe(sample,now) && !policy.Observe(new(24,10,5,2,10),now.AddSeconds(1)) &&
            !policy.Observe(sample,now.AddSeconds(2)) && !policy.Observe(sample,now.AddSeconds(3)),"a healthy fresh sample resets congestion so isolated spikes do not accumulate");
        policy=new(); policy.Observe(sample,now); policy.Observe(sample,now.AddSeconds(5)); policy.Observe(sample,now.AddSeconds(10));
        check(!policy.Observe(sample,now.AddSeconds(15)) && !policy.Observe(sample,now.AddSeconds(20)) && !policy.Observe(sample,now.AddSeconds(25)),"agent cooldown prevents frequent inference and repeated quality changes");
        check(PerformancePolicy.ParseProfile("{\"profile\":\"0\"}",2,0,2)==null && PerformancePolicy.ParseProfile("{\"profile\":null}",2,0,2)==null && PerformancePolicy.ParseProfile("{\"profile\":0}",4,0,2)==null,"agent rejects wrong JSON types and profiles outside the configured range");

        options=new AiOptions();
        var thinking=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var performance=new PerformanceAgent(()=>options,(_,_,_)=>thinking.Task);
        await performance.Evaluate(sample,now,()=>2,()=>true,CancellationToken.None);
        await performance.Evaluate(sample,now.AddSeconds(5),()=>2,()=>true,CancellationToken.None);
        var suggestion=performance.Evaluate(sample,now.AddSeconds(10),()=>2,()=>true,CancellationToken.None);
        performance.ManualOverride(); thinking.SetResult("{\"profile\":0}");
        check(await suggestion==null,"manual quality selection invalidates a pending agent result");
        thinking=new(TaskCreationOptions.RunContinuationsAsynchronously);
        performance=new(()=>options,(_,_,_)=>thinking.Task);
        await performance.Evaluate(sample,now,()=>2,()=>true,CancellationToken.None);
        await performance.Evaluate(sample,now.AddSeconds(5),()=>2,()=>true,CancellationToken.None);
        suggestion=performance.Evaluate(sample,now.AddSeconds(10),()=>2,()=>true,CancellationToken.None);
        options=options with {PerformanceAgent=false}; thinking.SetResult("{\"profile\":0}");
        check(await suggestion==null,"changing AI preferences invalidates a pending agent result");
        options=new(); int minimumCalls=0;
        performance=new(()=>options,(_,_,_)=> {minimumCalls++; return Task.FromResult<string?>("{\"profile\":0}");});
        for(int i=0;i<5;i++) await performance.Evaluate(sample with {Profile=0},now.AddSeconds(i),()=>0,()=>true,CancellationToken.None);
        check(minimumCalls==0,"agent does not spend inference resources when quality is already at its minimum");
        using(var foreground=await AiWork.Enter(false,CancellationToken.None))
            check(await AiWork.Enter(true,CancellationToken.None)==null,"background inference skips when foreground AI owns the shared gate");
        using(var background=await AiWork.Enter(true,CancellationToken.None))
            check(background!=null && await AiWork.Enter(true,CancellationToken.None)==null,"multiple viewers cannot run background inference concurrently");
        using var numeric=JsonDocument.Parse(PerformancePolicy.Messages(sample,0,2)[1].Content);
        check(numeric.RootElement.EnumerateObject().Select(p=>p.Name).SequenceEqual(["fps","rttMs","hostMs","currentProfile","minimum","maximum"]) &&
            numeric.RootElement.EnumerateObject().All(p=>p.Value.ValueKind==JsonValueKind.Number),"performance prompts contain only the six allowed numeric metrics");

        int cloudCalls=0;
        using var http=new HttpClient(new Handler(async request=>
        {
            cloudCalls++;
            string body=await request.Content!.ReadAsStringAsync(); var json=JsonNode.Parse(body)!;
            bool anthropic=request.RequestUri!.Host=="api.anthropic.com";
            check(request.RequestUri.Scheme=="https" && request.Headers.Authorization?.Parameter==(anthropic?"fixture-claude":"fixture-openai") &&
                !body.Contains("fixture-openai") && !body.Contains("fixture-claude"),"cloud credentials stay in the correct fixed-host authorization header");
            if(anthropic)
            {
                check(request.RequestUri.AbsolutePath=="/v1/messages" && request.Headers.Contains("anthropic-version") && json["max_tokens"]!.GetValue<int>()==1200,"Claude uses Messages with a bounded output budget");
                return new(HttpStatusCode.OK) {Content=new StringContent("""{"content":[{"type":"thinking","thinking":"hidden"},{"type":"text","text":"claude answer"}]}""")};
            }
            check(request.RequestUri.AbsolutePath=="/v1/responses" && json["store"]!.GetValue<bool>()==false && json["max_output_tokens"]!.GetValue<int>()==1200,"OpenAI uses Responses without stored response state and with bounded output");
            return new(HttpStatusCode.OK) {Content=new StringContent("""{"status":"completed","output":[{"type":"reasoning"},{"type":"message","content":[{"type":"output_text","text":"open answer"}]}]}""")};
        }));
        var openProvider=new CloudAiProvider(http,false); var claudeProvider=new CloudAiProvider(http,true);
        check((await openProvider.Chat("gpt-fixture","fixture-openai",[new("system","instruction"),new("user","hi")],null,CancellationToken.None)).Text=="open answer","OpenAI parses text after non-message output items");
        check((await claudeProvider.Chat("claude-fixture","fixture-claude",[new("user","hi")],null,CancellationToken.None)).Text=="claude answer" && cloudCalls==2,"Claude parses text while ignoring thinking blocks");

        using var remoteHttp=new HttpClient(new Handler(request=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(request.RequestUri!.AbsolutePath=="/api/tags"?
            """{"models":[{"name":"innocent:latest","size":1000}]}""":"""{"remote_host":"https://ollama.com","capabilities":["completion"]}""")})));
        var remoteOllama=new Ollama(remoteHttp);
        check((await remoteOllama.Models(CancellationToken.None)).Count==0,"Ollama show metadata blocks remotely backed models even with innocent local tags");
        bool blocked=false; try {await new OllamaProvider(remoteOllama).Chat("innocent:latest","",[],null,CancellationToken.None);} catch(InvalidDataException) {blocked=true;}
        check(blocked,"manually configured model IDs cannot bypass local metadata verification");

        const string catalog="""<a href="/library/tiny"><p>Local &amp; useful</p><span>tools</span><span>1.7b</span><span title="Oct 29, 2026 1:43 AM UTC">yesterday</span></a><a href="/library/hosted"><p>Hosted</p><span>cloud</span></a><a href="/library/embedding"><p>Vectors</p><span>embedding</span><span>300m</span></a><a href="/library/mixed"><p>Both</p><span>cloud</span><span>9b</span></a><a href="/library/tiny"><p>duplicate</p></a>""";
        var parsed=OllamaCatalog.Parse(catalog);
        check(parsed.Select(m=>m.Name).SequenceEqual(["tiny","mixed"]) && parsed[0].Updated!=null && parsed[0].Description=="Local & useful","catalog scanning excludes cloud-only/embedding models, deduplicates names and reads update metadata");
        check(parsed.All(m=>m.Page.Host=="ollama.com") && OllamaCatalog.Parse("<a href=\"https://evil.example/library/inject\">x</a>").Count==0,"catalog links are restricted to the official library");
    }
}
