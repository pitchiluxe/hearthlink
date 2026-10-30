using HearthLink.Core;
using System.Text.Json.Nodes;

namespace HearthLink;
/// <summary>App-owned clients. Keys come only from protected local preferences, never paired PCs.</summary>
public sealed class AiRuntime : IDisposable
{
    readonly HttpClient localClient,cloudClient=CloudAiProvider.CreateClient(),catalogClient=CloudAiProvider.CreateClient();
    readonly Func<AiOptions> options;
    readonly AiRouter router;
    public Ollama Local {get;}
    public CloudAiProvider OpenAI {get;}
    public CloudAiProvider Claude {get;}
    public OllamaCatalog Catalog {get;}
    public AiOptions Options=>options();
    public AiRuntime(Func<AiOptions> settings,HttpClient? local=null)
    {
        localClient=local??Ollama.CreateClient();
        options=settings; Local=new(localClient); OpenAI=new(cloudClient,false); Claude=new(cloudClient,true); Catalog=new(catalogClient);
        router=new(settings,new OllamaProvider(Local),OpenAI,Claude);
    }
    public async Task<AiReply> Chat(IReadOnlyList<AiMessage> messages,JsonNode? format,CancellationToken token,string? model=null)
    {
        using var lease=await AiWork.Enter(false,token);
        return await router.Chat(messages,format,token,model);
    }
    public async Task<string?> Background(PerformanceSample sample,AiOptions config,CancellationToken token,Action<string>? status=null)
    {
        using var lease=await AiWork.Enter(true,token); if(lease==null) {status?.Invoke("Local AI waiting: another AI request is active"); return null;}
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var models=await Local.Models(timeout.Token);
        var model=models.Where(m=>m.Size is >0 and <=3L*1024*1024*1024).OrderBy(m=>m.Size).FirstOrDefault();
        if(model==null) {status?.Invoke("Local AI unavailable: install a chat model up to 3 GiB"); return null;}
        if(await Local.Inspect(model.Name,timeout.Token)==null) {status?.Invoke("Local AI model verification failed; quality unchanged"); return null;}
        status?.Invoke("Local AI checking numeric timings");
        string answer=await Local.Chat(model.Name,PerformancePolicy.Messages(sample,config.MinimumQuality,config.MaximumQuality),
            JsonNode.Parse("""{"type":"object","required":["profile"],"properties":{"profile":{"type":"integer","minimum":0,"maximum":4}},"additionalProperties":false}"""),timeout.Token,true);
        status?.Invoke("Local AI monitoring (no cloud)"); return answer;
    }
    public void Dispose() {localClient.Dispose(); cloudClient.Dispose(); catalogClient.Dispose();}
}
