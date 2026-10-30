using HearthLink.Core;

namespace HearthLink;
public sealed class AiSettingsPanel : UserControl
{
    readonly IdentityStore identity;
    readonly AiRuntime runtime;
    readonly CancellationTokenSource lifetime=new();
    readonly ComboBox installed=new() {Name="OllamaModel",DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly CheckBox cloud=new() {Name="CloudFallback",Text="Enable cloud fallback: Ollama → OpenAI → Claude",AutoSize=true,UseMnemonic=false};
    readonly TextBox openKey=new() {Name="OpenAIKey",UseSystemPasswordChar=true,Dock=DockStyle.Fill,MaxLength=4096};
    readonly TextBox claudeKey=new() {Name="ClaudeKey",UseSystemPasswordChar=true,Dock=DockStyle.Fill,MaxLength=4096};
    readonly ComboBox openModel=new() {Name="OpenAIModel",Dock=DockStyle.Fill,MaxLength=160};
    readonly ComboBox claudeModel=new() {Name="ClaudeModel",Dock=DockStyle.Fill,MaxLength=160};
    readonly CheckBox agent=new() {Name="PerformanceAgent",Text="Let the local AI agent reduce image quality during sustained congestion",AutoSize=true};
    readonly ComboBox minimum=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=180};
    readonly ComboBox maximum=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=180};
    readonly Label state=TextNote("Ollama is primary. Cloud requests stay off until you enable and save fallback.");
    readonly Label modelState=TextNote("Scan installed models to verify local chat capability. No model downloads run automatically.");
    readonly TextBox scanDetails=new() {Name="OllamaScanDetails",Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Text="Models that cannot be verified will be listed here. A failed model will not block healthy models."};
    readonly Button cancelScan=Brand.Style(new Button {Text="Cancel scan",Enabled=false,AutoSize=true});
    CancellationTokenSource? scanStop;
    bool scanningModels;
    readonly DataGridView catalog=new() {Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
        RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.FixedSingle};
    readonly TextBox search=new() {PlaceholderText="Filter discovered models",Dock=DockStyle.Fill};
    IReadOnlyList<CatalogModel> discovered=[];
    bool working;
    public AiSettingsPanel(IdentityStore store,AiRuntime ai)
    {
        identity=store; runtime=ai; Dock=DockStyle.Fill; BackColor=Color.White;
        var settings=store.Config.Ai??new();
        installed.Items.Add("Automatic (local chat models)"); installed.SelectedIndex=0;
        if(!string.IsNullOrEmpty(settings.OllamaModel)) {installed.Items.Add(settings.OllamaModel); installed.SelectedIndex=1;}
        cloud.Checked=settings.CloudFallback; openKey.Text=settings.OpenAIKey; claudeKey.Text=settings.ClaudeKey;
        openModel.Text=settings.OpenAIModel; claudeModel.Text=settings.ClaudeModel; agent.Checked=settings.PerformanceAgent;
        minimum.Items.AddRange(DesktopCapture.Profiles); maximum.Items.AddRange(DesktopCapture.Profiles);
        minimum.SelectedIndex=Math.Clamp(settings.MinimumQuality,0,4); maximum.SelectedIndex=Math.Clamp(settings.MaximumQuality,minimum.SelectedIndex,4);
        catalog.Columns.Add("Name","Model"); catalog.Columns.Add("Variants","Local sizes"); catalog.Columns.Add("Updated","Updated");
        search.TextChanged+=(_,_)=>ShowCatalog();
        cancelScan.Click+=(_,_)=>scanStop?.Cancel();
        var rows=new (Control Control,int Height)[] {
            (Heading("Ollama · primary"),38),(installed,34),(Buttons(Button("Scan installed models",ScanInstalled),cancelScan,Button("Ollama setup",()=>Open("https://ollama.com/download"))),44),(modelState,54),(scanDetails,92),
            (Heading("Cloud fallback · optional"),38),(cloud,36),
            (TextNote("When enabled, file-helper prompts, conversation and folder/path metadata may be sent to OpenAI or Claude. File contents and screenshots are not sent. APIs may charge separately."),70),
            (TextNote("OpenAI API key (stored with Windows user protection)"),30),(openKey,34),(openModel,34),
            (Buttons(Button("List OpenAI models",()=>LoadCloud(false)),Button("OpenAI API keys",()=>Open("https://platform.openai.com/api-keys"))),44),
            (TextNote("Claude API key (stored with Windows user protection)"),30),(claudeKey,34),(claudeModel,34),
            (Buttons(Button("List Claude models",()=>LoadCloud(true)),Button("Claude API keys",()=>Open("https://platform.claude.com/settings/keys"))),44),
            (TextNote("Select an accessible text model, or enter its exact API model ID. Enable fallback and Save AI settings before listing cloud models."),54),
            (Heading("Local performance agent"),38),(agent,42),
            (TextNote("Uses numeric session timings only, a small installed local model (up to 3 GiB), and no cloud fallback. One bounded request per two minutes; a manual quality choice pauses it for that session."),66),
            (TextNote("Allowed range: minimum on the left, maximum on the right. The agent only reduces quality, one step at a time; it never changes security or files."),58),
            (Buttons(minimum,maximum),40),
            (Heading("Discover recent local models"),38),
            (TextNote("Scan public metadata from Ollama Library, newest first. Local variants require hardware and license checks; hosted-only and embedding models are excluded. Scanning does not download models."),68),
            (Buttons(Button("Scan Ollama Library",ScanCatalog),Button("Open full library",()=>Open("https://ollama.com/library?sort=newest"))),46),
            (search,34),(catalog,260),(Buttons(Button("Model details / license",OpenModel),Button("Copy install command",CopyInstall)),46)
        };
        var stack=new TableLayoutPanel {Dock=DockStyle.Top,ColumnCount=1,RowCount=rows.Length,Height=rows.Sum(r=>r.Height),Padding=new(8),Margin=Padding.Empty};
        stack.ColumnStyles.Add(new(SizeType.Percent,100));
        for(int i=0;i<rows.Length;i++) {stack.RowStyles.Add(new(SizeType.Absolute,rows[i].Height)); rows[i].Control.Dock=DockStyle.Fill; rows[i].Control.Margin=new(0,0,0,4); stack.Controls.Add(rows[i].Control,0,i);}
        var scroll=new Panel {Dock=DockStyle.Fill,AutoScroll=true}; scroll.Controls.Add(stack); Controls.Add(scroll);
        state.Dock=DockStyle.Bottom; state.Height=70; Controls.Add(state);
        var saveBar=new Panel {Dock=DockStyle.Top,Height=48,Padding=new(0,4,0,4)};
        var saveButton=Button("Save AI settings",Save); saveButton.Dock=DockStyle.Left; saveButton.Width=160;
        saveBar.Controls.Add(TextNote("Save before listing cloud models. Cloud fallback is off unless you enable it.")); saveBar.Controls.Add(saveButton); Controls.Add(saveBar);
        Disposed+=(_,_)=>lifetime.Cancel();
    }
    static Label TextNote(string text)=>new() {Text=text,UseMnemonic=false,ForeColor=Brand.Muted,AutoEllipsis=true,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
    static Label Heading(string text)=>new() {Text=text,UseMnemonic=false,ForeColor=Brand.Ink,Font=new("Segoe UI",13,FontStyle.Bold),TextAlign=ContentAlignment.MiddleLeft};
    static FlowLayoutPanel Buttons(params Control[] controls) {var row=new FlowLayoutPanel {WrapContents=false,Margin=Padding.Empty}; row.Controls.AddRange(controls); return row;}
    Button Button(string label,Action action)=>Button(label,()=> {action(); return Task.CompletedTask;});
    Button Button(string label,Func<Task> action)
    {
        var button=Brand.Style(new Button {Text=label,UseMnemonic=false,AutoSize=true,Margin=new(0,0,8,0)});
        button.Click+=async(_,_)=>
        {
            if(working) return; working=true; button.Enabled=false;
            try {await action();}
            catch(OperationCanceledException) {if(!IsDisposed) state.Text="AI operation cancelled or timed out. Existing settings and installed models are unchanged.";}
            catch(Exception e) when(e is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException or TimeoutException)
            {if(!IsDisposed) state.Text="AI operation did not complete. Check the provider, credentials, model access and internet connection. No fallback was enabled automatically.";}
            catch(Exception e) when(e is UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
            {if(!IsDisposed) state.Text="Windows could not complete this action. Check profile-folder access, browser or clipboard availability. Existing saved settings are unchanged.";}
            finally {working=false; if(!button.IsDisposed) button.Enabled=true;}
        };
        return button;
    }
    void Save()
    {
        if(minimum.SelectedIndex>maximum.SelectedIndex) {state.Text="Minimum quality must not exceed maximum quality."; return;}
        var prior=identity.Config.Ai;
        identity.Config.Ai=new() {OllamaModel=installed.SelectedIndex<=0?"":installed.Text,CloudFallback=cloud.Checked,
            OpenAIKey=openKey.Text.Trim(),OpenAIModel=openModel.Text.Trim(),ClaudeKey=claudeKey.Text.Trim(),ClaudeModel=claudeModel.Text.Trim(),
            PerformanceAgent=agent.Checked,MinimumQuality=minimum.SelectedIndex,MaximumQuality=maximum.SelectedIndex};
        try {identity.Save(); state.Text="AI settings saved. Ollama is primary; cloud fallback is "+(cloud.Checked?"enabled for the file helper.":"off. Requests stay local.");}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {identity.Config.Ai=prior; state.Text="AI settings could not be saved. Check disk space and profile-folder access. Previous credentials and preferences are unchanged.";}
    }
    async Task ScanInstalled()
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        scanStop=stop; scanningModels=true; cancelScan.Enabled=true;
        modelState.Text="Reading installed models from Ollama…"; state.Text="Local model scan in progress. No cloud credentials or internet are required.";
        try
        {
            var progress=new Progress<OllamaScanProgress>(p=>
            {if(!IsDisposed && scanningModels && scanStop==stop) modelState.Text=$"Checked {p.Checked} of {p.Total}: {p.Model}";});
            var result=await runtime.Local.ScanModels(stop.Token,progress); if(IsDisposed) return;
            string selected=installed.SelectedIndex<=0?"":installed.Text;
            installed.Items.Clear(); installed.Items.Add("Automatic (local chat models)");
            foreach(var model in result.Models.OrderByDescending(m=>m.Modified)) installed.Items.Add(model.Name);
            installed.SelectedItem=selected; if(installed.SelectedIndex<0) installed.SelectedIndex=0;
            modelState.Text=$"{result.Models.Count} verified local chat models; {result.Issues.Count} could not be checked. "+(result.Partial?"Partial scan; retry for unchecked models.":"Choose one and Save AI settings.");
            scanDetails.Text=result.Issues.Count==0?"No unreadable models found. Cloud and non-chat models are excluded.":
                string.Join(Environment.NewLine+Environment.NewLine,result.Issues.Select(issue=>issue.Model+": "+issue.Reason));
            state.Text=result.Issues.Count>0?"Scan finished with warnings. Healthy models are available; see the model details above for entries needing repair.":
                result.Models.Count==0?"Ollama is responding, but no verified local chat models were found. Install a local chat model in Ollama, then retry.":"Local model scan complete. No models were downloaded or changed.";
        }
        catch(OperationCanceledException) {if(!IsDisposed) {modelState.Text="Scan cancelled. The previous model list was kept."; state.Text=modelState.Text;}}
        catch(HttpRequestException) {if(!IsDisposed) {modelState.Text="Ollama is not responding on this PC. Start the Ollama app, then retry."; state.Text="Cannot reach local Ollama. This scan does not need API keys or internet access.";}}
        catch(AiUnavailableException) {if(!IsDisposed) {modelState.Text="Ollama's model list is unavailable or timed out. Restart Ollama, then retry."; state.Text=modelState.Text;}}
        catch(Exception e) when(e is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {if(!IsDisposed) {modelState.Text="Ollama returned an unreadable model list. Check or update Ollama, then retry."; state.Text=modelState.Text;}}
        finally {scanningModels=false; scanStop=null; if(!cancelScan.IsDisposed) cancelScan.Enabled=false;}
    }
    async Task LoadCloud(bool anthropic)
    {
        var config=runtime.Options;
        if(!cloud.Checked || !config.CloudFallback) {state.Text="Enable cloud fallback and Save AI settings before contacting a cloud provider."; return;}
        var provider=anthropic?runtime.Claude:runtime.OpenAI;
        var found=await provider.Models(anthropic?config.ClaudeKey:config.OpenAIKey,lifetime.Token);
        if(IsDisposed) return;
        var combo=anthropic?claudeModel:openModel; string prior=combo.Text;
        combo.Items.Clear(); foreach(string name in found) combo.Items.Add(name); combo.Text=prior;
        state.Text=$"{found.Count} API model IDs listed. Choose a text model supported by your account, then Save AI settings.";
    }
    async Task ScanCatalog()
    {
        state.Text="Scanning official Ollama Library metadata…";
        var scan=await runtime.Catalog.Scan(lifetime.Token); if(IsDisposed) return;
        discovered=scan.Models; ShowCatalog();
        state.Text=$"{discovered.Count} local-capable catalog entries scanned {scan.Scanned:t}. "+(scan.Partial?"Scan limit reached; open the full library for more.":"Verify tags, license and hardware on each model page. No models downloaded.");
    }
    void ShowCatalog()
    {
        catalog.Rows.Clear();
        foreach(var model in discovered.Where(m=>(m.Name+" "+m.Description).Contains(search.Text,StringComparison.OrdinalIgnoreCase)))
        {
            int row=catalog.Rows.Add(model.Name,model.Variants,model.Updated?.ToLocalTime().ToString("d")??"See model page");
            catalog.Rows[row].Tag=model; catalog.Rows[row].Cells[0].ToolTipText=model.Description;
        }
    }
    CatalogModel? Selected()=>catalog.SelectedRows.Count==0?null:catalog.SelectedRows[0].Tag as CatalogModel;
    void OpenModel() {if(Selected() is {} model) Open(model.Page.AbsoluteUri);}
    void CopyInstall()
    {
        if(Selected() is not {} model) return;
        Clipboard.SetText("ollama pull "+model.Name);
        state.Text="Install command copied. Review the model's license and size, run it in your terminal, then Scan installed models. Nothing downloaded by HearthLink.";
    }
    static void Open(string url)=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) {UseShellExecute=true});
}
