using HearthLink;
using HearthLink.Core;
using System.Net;
using System.Reflection;

static class ModelScanUiChecks
{
    sealed class Handler(bool offline) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(offline) throw new HttpRequestException("fixture: Ollama is stopped");
            if(request.RequestUri!.AbsolutePath=="/api/tags") return new(HttpStatusCode.OK) {Content=new StringContent("""{"models":[{"name":"healthy:latest","size":1000},{"name":"broken:latest","size":2000}]}""")};
            string body=await request.Content!.ReadAsStringAsync(token);
            return body.Contains("broken:latest")?new(HttpStatusCode.InternalServerError) {Content=new StringContent("""{"error":"missing model blob"}""")}:
                new(HttpStatusCode.OK) {Content=new StringContent("""{"capabilities":["completion"]}""")};
        }
    }
    public static async Task Run(MainWindow owner,Action<bool,string> check)
    {
        string folder=Path.Combine(Path.GetTempPath(),"HearthLink-scan-ui-"+Guid.NewGuid().ToString("N"));
        var identity=new IdentityStore(folder);
        try
        {
            foreach(bool offline in new[]{false,true})
            {
                using var runtime=new AiRuntime(()=>new(),new HttpClient(new Handler(offline)));
                var panel=await owner.OnUi(()=>new AiSettingsPanel(identity,runtime));
                try
                {
                    var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                    await owner.OnUi(()=>(Task)typeof(AiSettingsPanel).GetMethod("ScanInstalled",flags)!.Invoke(panel,null)!).Unwrap();
                    await owner.OnUi(()=>
                    {
                        var state=(Label)typeof(AiSettingsPanel).GetField("modelState",flags)!.GetValue(panel)!;
                        var details=(TextBox)typeof(AiSettingsPanel).GetField("scanDetails",flags)!.GetValue(panel)!;
                        var models=(ComboBox)typeof(AiSettingsPanel).GetField("installed",flags)!.GetValue(panel)!;
                        check(offline?state.Text.Contains("Start the Ollama app"):
                            models.Items.Contains("healthy:latest") && !models.Items.Contains("broken:latest") && details.Text.Contains("broken:latest"),
                            offline?"offline Ollama scan shows a local-start instruction rather than cloud credential advice":"installed-model UI keeps healthy models and identifies the broken entry");
                        check(!state.Text.Contains("Verifying") && !state.Text.StartsWith("Reading installed"),"scan clears its pending label after success or failure");
                        return true;
                    });
                }
                finally {await owner.OnUi(()=> {panel.Dispose(); return true;});}
            }
        }
        finally {identity.Certificate.Dispose(); Directory.Delete(folder,true);}
    }
}
