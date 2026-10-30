using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HearthLink.Core;

public sealed record AiOptions
{
    public string OllamaModel {get;init;}="";
    public bool CloudFallback {get;init;}
    public string OpenAIKey {get;init;}="";
    public string OpenAIModel {get;init;}="";
    public string ClaudeKey {get;init;}="";
    public string ClaudeModel {get;init;}="";
    public bool PerformanceAgent {get;init;}=true;
    public int MinimumQuality {get;init;}
    public int MaximumQuality {get;init;}=2;
    public override string ToString()=>"AI settings (credentials hidden)";
}
public sealed record AiReply(string Text,string Provider,string Model);
public sealed class AiUnavailableException(string message) : InvalidOperationException(message);
public interface IAiProvider
{
    Task<AiReply> Chat(string model,string key,IReadOnlyList<AiMessage> messages,JsonNode? format,CancellationToken token);
}
public sealed class AiRouter(Func<AiOptions> options,IAiProvider ollama,IAiProvider openai,IAiProvider claude)
{
    public async Task<AiReply> Chat(IReadOnlyList<AiMessage> messages,JsonNode? format,CancellationToken token,string? localModel=null)
    {
        if(messages.Count>16 || messages.Sum(m=>m.Content.Length)>48_000) throw new InvalidDataException("AI request is too large. Start a new conversation.");
        token.ThrowIfCancellationRequested();
        try {return await ollama.Chat(localModel??options().OllamaModel,"",messages,format,token);}
        catch(AiUnavailableException) { }
        token.ThrowIfCancellationRequested();
        var config=options();
        if(config.CloudFallback && !string.IsNullOrWhiteSpace(config.OpenAIKey) && !string.IsNullOrWhiteSpace(config.OpenAIModel))
        {
            try {return await openai.Chat(config.OpenAIModel,config.OpenAIKey,messages,format,token);}
            catch(AiUnavailableException) { }
        }
        token.ThrowIfCancellationRequested(); config=options();
        if(config.CloudFallback && !string.IsNullOrWhiteSpace(config.ClaudeKey) && !string.IsNullOrWhiteSpace(config.ClaudeModel))
            return await claude.Chat(config.ClaudeModel,config.ClaudeKey,messages,format,token);
        throw new AiUnavailableException("Ollama is unavailable and no permitted cloud fallback completed. Check AI providers in Settings.");
    }
}

/// <summary>One inference gate per process: background work skips while another AI request is active.</summary>
public static class AiWork
{
    static readonly SemaphoreSlim gate=new(1,1);
    public static async Task<IDisposable?> Enter(bool background,CancellationToken token)
    {
        bool entered=background?await gate.WaitAsync(0,token):await Wait(token);
        return entered?new Lease():null;
    }
    static async Task<bool> Wait(CancellationToken token) {await gate.WaitAsync(token); return true;}
    sealed class Lease : IDisposable
    {
        bool disposed;
        public void Dispose() {if(!disposed) {disposed=true; gate.Release();}}
    }
}

public sealed class OllamaProvider(Ollama ollama) : IAiProvider
{
    public async Task<AiReply> Chat(string model,string key,IReadOnlyList<AiMessage> messages,JsonNode? format,CancellationToken token)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(65));
        try
        {
            if(string.IsNullOrWhiteSpace(model)) model=Ollama.Choose(await ollama.Models(timeout.Token))?.Name??throw new AiUnavailableException("No local chat model is installed.");
            if(await ollama.Inspect(model,timeout.Token) is null) throw new InvalidDataException("The selected Ollama model is not verified as a local chat model.");
            return new(await ollama.Chat(model,messages,format,timeout.Token),"Ollama",model);
        }
        catch(HttpRequestException) {throw new AiUnavailableException("Ollama cannot be reached on this PC.");}
        catch(OperationCanceledException) when(!token.IsCancellationRequested) {throw new AiUnavailableException("Ollama did not answer within 65 seconds.");}
    }
}

public sealed class CloudAiProvider(HttpClient client,bool anthropic) : IAiProvider
{
    public static HttpClient CreateClient()=>new(new HttpClientHandler {AllowAutoRedirect=false}) {Timeout=Timeout.InfiniteTimeSpan};
    string Name=>anthropic?"Claude":"OpenAI";
    Uri Endpoint(string suffix)=>new((anthropic?"https://api.anthropic.com/v1/":"https://api.openai.com/v1/")+suffix);
    HttpRequestMessage Request(HttpMethod method,string endpoint,string key)
    {
        if(string.IsNullOrWhiteSpace(key) || key.Length>4096 || key.Any(char.IsControl)) throw new InvalidDataException("Invalid API key. Check Settings.");
        var request=new HttpRequestMessage(method,Endpoint(endpoint));
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        if(anthropic) request.Headers.Add("anthropic-version","2026-10-21");
        return request;
    }
    public async Task<IReadOnlyList<string>> Models(string key,CancellationToken token)
    {
        using var request=Request(HttpMethod.Get,"models",key);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        var root=JsonNode.Parse(await AiHttp.Read(response,Name,1024*1024,timeout.Token));
        return (root?["data"]?.AsArray()??[]).Select(m=>m?["id"]?.GetValue<string>()??"")
            .Where(ValidModel).Where(id=>anthropic?id.StartsWith("claude-",StringComparison.Ordinal):
                (id.StartsWith("gpt-",StringComparison.Ordinal) || id.StartsWith("o",StringComparison.Ordinal)) &&
                !new[]{"image","audio","realtime","transcribe","tts","embedding","moderation"}.Any(s=>id.Contains(s,StringComparison.OrdinalIgnoreCase)))
            .Distinct().Order(StringComparer.Ordinal).Take(300).ToArray();
    }
    static bool ValidModel(string model)=>model.Length is >0 and <=160 && model.All(c=>char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    public async Task<AiReply> Chat(string model,string key,IReadOnlyList<AiMessage> messages,JsonNode? format,CancellationToken token)
    {
        if(!ValidModel(model)) throw new InvalidDataException("Choose a valid API model in Settings.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            string instructions=string.Join("\n",messages.Where(m=>m.Role=="system").Select(m=>m.Content));
            if(format!=null) instructions+="\nRespond with JSON only, matching this schema: "+format.ToJsonString();
            var body=anthropic?new JsonObject {
                ["model"]=model,["max_tokens"]=1200,["system"]=instructions,
                ["messages"]=new JsonArray(messages.Where(m=>m.Role!="system").Select(m=>(JsonNode)new JsonObject {["role"]=m.Role=="assistant"?"assistant":"user",["content"]=m.Content}).ToArray())
            }:new JsonObject {
                ["model"]=model,["max_output_tokens"]=1200,["store"]=false,["instructions"]=instructions,
                ["input"]=new JsonArray(messages.Where(m=>m.Role!="system").Select(m=>(JsonNode)new JsonObject {["role"]=m.Role=="assistant"?"assistant":"user",["content"]=m.Content}).ToArray())
            };
            using var request=Request(HttpMethod.Post,anthropic?"messages":"responses",key);
            request.Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            var root=JsonNode.Parse(await AiHttp.Read(response,Name,256*1024,timeout.Token));
            if(!anthropic && root?["status"]?.GetValue<string>() is {} status && status!="completed") throw new InvalidDataException("OpenAI returned an incomplete response.");
            var text=new StringBuilder();
            if(anthropic)
            {
                foreach(var block in root?["content"]?.AsArray()??[])
                    if(block?["type"]?.GetValue<string>()=="text") text.AppendLine(block["text"]?.GetValue<string>());
            }
            else
            {
                foreach(var item in root?["output"]?.AsArray()??[])
                    if(item?["type"]?.GetValue<string>()=="message")
                        foreach(var block in item["content"]?.AsArray()??[])
                            if(block?["type"]?.GetValue<string>()=="output_text") text.AppendLine(block["text"]?.GetValue<string>());
            }
            string answer=text.ToString().Trim();
            if(answer.Length is <1 or >32_000) throw new InvalidDataException(Name+" returned no usable text or too much text.");
            return new(answer,Name,model);
        }
        catch(HttpRequestException) {throw new AiUnavailableException(Name+" cannot be reached.");}
        catch(OperationCanceledException) when(!token.IsCancellationRequested) {throw new AiUnavailableException(Name+" timed out.");}
        catch(JsonException) {throw new InvalidDataException(Name+" returned invalid JSON.");}
    }
}
public static class AiHttp
{
    public static async Task<string> Read(HttpResponseMessage response,string provider,int maximum,CancellationToken token)
    {
        int status=(int)response.StatusCode;
        if(!response.IsSuccessStatusCode)
        {
            if(status is 401 or 403 or 404 or 408 or 429 || status>=500) throw new AiUnavailableException($"{provider} unavailable (HTTP {status}). Check credentials, model access or account limits.");
            throw new InvalidDataException($"{provider} rejected the request (HTTP {status}). Check its configuration.");
        }
        if(response.Content.Headers.ContentLength>maximum) throw new InvalidDataException(provider+" response is too large.");
        await using var input=await response.Content.ReadAsStreamAsync(token);
        using var output=new MemoryStream(); byte[] buffer=new byte[8192];
        for(int read;(read=await input.ReadAsync(buffer,token))>0;)
        {if(output.Length+read>maximum) throw new InvalidDataException(provider+" response is too large."); output.Write(buffer,0,read);}
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
