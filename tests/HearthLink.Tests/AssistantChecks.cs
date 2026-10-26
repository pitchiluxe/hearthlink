using HearthLink.Core;
using System.Net;
using System.Text.Json.Nodes;

static class AssistantChecks
{
    sealed class Handler(Func<HttpRequestMessage,string?,HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>
            respond(request,request.Content==null?null:await request.Content.ReadAsStringAsync(token));
    }
    public static async Task Run(Action<bool,string> check)
    {
        check(TransferAssistant.Skips(["node_modules"],"Node_Modules",true) && TransferAssistant.Skips(["*.mp4"],"clip.MP4",false)
            && !TransferAssistant.Skips(["*.mp4"],"videos.mp4",true) && !TransferAssistant.Skips(["bin"],"binary.txt",false) && !TransferAssistant.Skips(null,"bin",true),"skip rules match exact names and file types only");
        check(TransferAssistant.ValidSkip("node_modules") && TransferAssistant.ValidSkip("*.mp4") && !TransferAssistant.ValidSkip("*") && !TransferAssistant.ValidSkip("..")
            && !TransferAssistant.ValidSkip("*.m*") && !TransferAssistant.ValidSkip("C:\\Windows"),"only plain names and *.ext patterns are accepted as skip rules");
        var heavy=TransferAssistant.Heavy(["App","App\\node_modules","App\\node_modules\\react","App\\node_modules\\react\\index.js","App\\src\\bin.cs","App\\.git\\HEAD"],1);
        check(heavy.SequenceEqual([("node_modules",2),(".git",1)]),"heavy folders count what is inside them, not a file named like one");

        FileEntry Folder(string n)=>new(n,true,0,DateTimeOffset.UnixEpoch); FileEntry File(string n)=>new(n,false,10,DateTimeOffset.UnixEpoch);
        IReadOnlyList<FileEntry> local=[Folder("ANNAIR-mobile"),File("notes.txt")], remote=[Folder("Work")];
        var (reply,plan)=TransferAssistant.ReadPlan("""{"action":"copy","direction":"to_remote","items":["C:/Users/erick/Projects/annair-mobile","ghost.txt"],"skip":["C:/x/node_modules","*.log","notes.txt","*","..\\.."],"reply":"Sending your app."}""",local,remote);
        check(plan is {ToRemote:true} && plan.Items.SequenceEqual(["ANNAIR-mobile"]) && plan.Skip.SequenceEqual(["node_modules","*.log","notes.txt"]) && reply=="Sending your app.","a plan keeps only listed items and safe skip rules");
        check(TransferAssistant.ReadPlan("""{"action":"copy","direction":"to_local","items":["notes.txt"],"skip":[],"reply":"ok"}""",local,remote).Plan==null,"items must exist on the side being copied from");
        check(TransferAssistant.ReadPlan("""{"action":"copy","direction":"to_remote","items":["ANNAIR-mobile"],"skip":["ANNAIR-mobile"],"reply":"ok"}""",local,remote).Plan!.Skip.Count==0,"a skip rule never cancels a picked item");
        check(TransferAssistant.ReadPlan("not json",local,remote) is ("not json",null) && TransferAssistant.ReadPlan("""Sure! {"action":"answer","direction":"to_remote","items":["notes.txt"],"skip":[],"reply":"Hi"}""",local,remote) is ("Hi",null),"answers without a copy never produce a plan");
        string described=TransferAssistant.Describe(new string('x',5000),"C:\\P",Enumerable.Range(0,400).Select(i=>File($"f{i}.txt")).ToArray(),["f1.txt"],"",[],[]);
        check(described.Contains("- …and 250 more") && described.Contains("Selected: f1.txt") && described.Contains("REMOTE folder: (not open)") && described.EndsWith(new string('x',TransferAssistant.MaxRequestLength)) && !described.Contains(new string('x',TransferAssistant.MaxRequestLength+1)),"the request lists a bounded part of each folder");

        var models=new[] {new AiModel("big",20L<<30,["completion","tools"],false),new AiModel("mid",6L<<30,["completion","tools"],false),new AiModel("small",1L<<30,["completion"],false)};
        check(Ollama.Choose(models)?.Name=="mid" && Ollama.Choose(models,"SMALL")?.Name=="small" && Ollama.Choose([models[2]])?.Name=="small" && Ollama.Choose([])==null,"model choice prefers a tools model that fits, or the one asked for");

        string? sent=null; Uri? asked=null;
        using var client=new HttpClient(new Handler((request,body)=>
        {
            asked=request.RequestUri;
            if(request.RequestUri!.AbsolutePath=="/api/tags") return new(HttpStatusCode.OK) {Content=new StringContent("""{"models":[{"name":"qwen3.5:latest","size":6600000000,"capabilities":["completion","tools"]},{"name":"gpt-oss:120b-cloud","size":1,"capabilities":["completion","tools"]},{"name":"x:latest","size":1,"remote_host":"https://ollama.com","capabilities":["completion"]},{"name":"nomic-embed-text:latest","size":1,"capabilities":["embedding"]},{"name":"llama2:latest","size":3800000000}]}""")};
            sent=body;
            if(body!.Contains("\"broken\"")) return new(HttpStatusCode.NotFound) {Content=new StringContent("""{"error":"model 'broken' not found"}""")};
            if(body.Contains("\"huge\"")) return new(HttpStatusCode.OK) {Content=new StringContent(new string(' ',(int)Ollama.MaxReplyBytes+1))};
            return new(HttpStatusCode.OK) {Content=new StringContent("""{"message":{"role":"assistant","content":" {\"action\":\"answer\"} "}}""")};
        }));
        var ollama=new Ollama(client);
        var found=await ollama.Models(CancellationToken.None);
        check(found.Select(m=>m.Name).SequenceEqual(["qwen3.5:latest","llama2:latest"]) && asked?.Host=="127.0.0.1" && asked.Port==11434,"cloud and embedding models are never offered, and only this PC's Ollama is asked");
        string answer=await ollama.Chat("qwen3.5:latest",[new("user","hi")],TransferAssistant.PlanFormat(),CancellationToken.None);
        var request=JsonNode.Parse(sent!)!;
        check(answer=="{\"action\":\"answer\"}" && request["think"]!.GetValue<bool>()==false && request["stream"]!.GetValue<bool>()==false && request["format"]?["required"]!=null && request["messages"]![0]!["content"]!.GetValue<string>()=="hi","chat sends one non-streaming request in the plan format");
        bool refused=false; try { await ollama.Chat("broken",[],null,CancellationToken.None); } catch(InvalidOperationException e) { refused=e.Message.Contains("not found"); }
        check(refused,"Ollama errors are passed on in its own words");
        refused=false; try { await ollama.Chat("huge",[],null,CancellationToken.None); } catch(InvalidDataException) { refused=true; }
        check(refused,"oversized replies are refused");
    }
}
