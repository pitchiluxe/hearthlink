using HearthLink.Core;
using System.Text.Json;

namespace HearthLink;
/// <summary>
/// The AI helper beside the file manager, run by Ollama on this PC. It turns a request in plain words into a copy plan
/// the user runs with one click, watches each batch (pointing out heavy folders such as node_modules) and explains failures.
/// It never moves anything by itself.
/// </summary>
public sealed class AssistantPanel : Panel
{
    readonly Ollama ollama=new(Ollama.CreateClient());
    readonly CancellationTokenSource lifetime=new();
    readonly ComboBox models=new() {Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList,Font=new("Segoe UI",9)};
    readonly Label state=new() {Dock=DockStyle.Top,Height=40,ForeColor=Brand.Muted,Font=new("Segoe UI",9),Padding=new(2,4,2,0)};
    readonly TextBox log=new() {Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White,BorderStyle=BorderStyle.None,Font=new("Segoe UI",9.5f)};
    readonly TextBox proposal=new() {Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Brand.Canvas,ForeColor=Brand.Ink,Font=new("Segoe UI",9,FontStyle.Bold)};
    readonly Button run=Brand.Style(new Button {Text="Run",Dock=DockStyle.Right,Width=70},true);
    readonly Button dismiss=Brand.Style(new Button {Text="✕",Dock=DockStyle.Right,Width=40});
    readonly Panel plan=new() {Dock=DockStyle.Bottom,Height=110,Padding=new(0,6,0,6),Visible=false};
    readonly TextBox ask=new() {Dock=DockStyle.Fill,PlaceholderText="Ask, e.g. copy ANNAIR-mobile without node_modules"};
    readonly Button send=Brand.Style(new Button {Text="Ask",Dock=DockStyle.Right,Width=64},true);
    readonly Func<string,IReadOnlyList<AiMessage>> context;
    readonly Func<TransferPlan,Task> execute;
    readonly Func<TransferPlan,string> explainPlan;
    readonly Func<string,string,(string Reply,TransferPlan? Plan)> read;
    readonly List<AiMessage> history=[];
    Func<Task>? pending;
    bool busy;
    /// <param name="describe">The chat for a request: instructions, then both folders and the request.</param>
    /// <param name="check">Checks the model's answer against the folders it was shown and the request.</param>
    public AssistantPanel(Func<string,IReadOnlyList<AiMessage>> describe,Func<string,string,(string Reply,TransferPlan? Plan)> check,Func<TransferPlan,Task> runPlan,Func<TransferPlan,string> explain)
    {
        context=describe; read=check; execute=runPlan; explainPlan=explain;
        Dock=DockStyle.Right; Width=340; BackColor=Brand.Canvas; Padding=new(8,8,8,4);
        var header=new Label {Text="AI helper",Dock=DockStyle.Top,Height=26,Font=new("Segoe UI",11,FontStyle.Bold),ForeColor=Brand.Ink};
        var retry=new LinkLabel {Text="Retry",Dock=DockStyle.Right,Width=60,Font=new("Segoe UI",9),TextAlign=ContentAlignment.MiddleRight};
        header.Controls.Add(retry);
        var logFrame=new Panel {Dock=DockStyle.Fill,BackColor=Color.White,Padding=new(6),BorderStyle=BorderStyle.FixedSingle};
        logFrame.Controls.Add(log);
        plan.Controls.AddRange([proposal,run,dismiss]);
        var input=new Panel {Dock=DockStyle.Bottom,Height=40,Padding=new(0,6,0,0)};
        input.Controls.AddRange([ask,send]);
        Controls.AddRange([logFrame,plan,input,state,models,header]);
        Brand.StyleInput(ask);
        new ToolTip().SetToolTip(models,"Ollama model on this PC. Cloud models are not listed, so file names stay on this PC.");
        retry.LinkClicked+=async(_,_)=>await Connect();
        send.Click+=async(_,_)=>await Ask();
        ask.KeyDown+=async(_,e)=> { if(e.KeyCode==Keys.Enter) { e.Handled=e.SuppressKeyPress=true; await Ask(); } };
        dismiss.Click+=(_,_)=>Offer(null,null);
        run.Click+=async(_,_)=> { var action=pending; Offer(null,null); if(action!=null) await action(); };
        Disposed+=(_,_)=> { lifetime.Cancel(); lifetime.Dispose(); };
        Say("Hi! Tell me what to copy and where, in your own words. I'll propose a plan; nothing moves until you press Run.");
    }
    string? Model=>models.SelectedItem as string;
    /// <summary>Finds Ollama and its models; the panel stays usable (with a hint) when Ollama is not running.</summary>
    public async Task Connect()
    {
        state.Text="Looking for Ollama on this PC…"; models.Items.Clear();
        try
        {
            var found=await ollama.Models(lifetime.Token);
            foreach(var m in found) models.Items.Add(m.Name);
            var chosen=Ollama.Choose(found,Environment.GetEnvironmentVariable("HEARTHLINK_OLLAMA_MODEL"));
            if(chosen==null) { state.Text="Ollama is running but has no local chat model. Run: ollama pull llama3.2"; return; }
            models.SelectedItem=chosen.Name;
            state.Text="Runs on this PC only. The first answer can take a minute while the model loads.";
        }
        catch(Exception e) when(e is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            if(!IsDisposed) state.Text="Ollama is not running. Start Ollama, then press Retry. (ollama.com)";
        }
    }
    void Say(string text,string who="Helper") { if(IsDisposed) return; log.AppendText((log.TextLength>0?Environment.NewLine+Environment.NewLine:"")+who+": "+text); log.SelectionStart=log.TextLength; log.ScrollToCaret(); }
    /// <summary>Shows one proposed action above the input with Run and ✕, replacing any earlier one; null clears it.</summary>
    void Offer(string? text,Func<Task>? action) { pending=action; proposal.Text=text??""; plan.Visible=action!=null; }
    async Task Ask()
    {
        string request=ask.Text.Trim();
        if(request.Length==0 || busy) return;
        if(Model==null) { Say("I need Ollama first. Start it, then press Retry at the top."); return; }
        ask.Clear(); Say(request,"You"); busy=true; send.Enabled=false; Offer(null,null);
        string before=state.Text; state.Text="Thinking…";
        try
        {
            var messages=context(request);
            // Earlier turns help follow-ups ("and the other folder too"); only the last few are kept.
            string answer=await ollama.Chat(Model,[messages[0],..history.TakeLast(6),..messages.Skip(1)],TransferAssistant.PlanFormat(),lifetime.Token);
            var (reply,found)=read(answer,request);
            history.Add(new("user",request)); history.Add(new("assistant",reply));
            Say(reply);
            if(found!=null) Offer(explainPlan(found),()=>execute(found));
        }
        catch(Exception e) when(e is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            if(!IsDisposed) Say("I could not reach Ollama: "+e.Message);
        }
        finally { if(!IsDisposed) { busy=false; send.Enabled=true; state.Text=before; ask.Focus(); } }
    }
    /// <summary>Watches a batch as it starts: points out heavy folders and offers to stop and copy again without them.</summary>
    public void Watch(TransferStrip.Batch batch,Func<IReadOnlyList<string>,Task>? redoWithout)
    {
        if(batch.Heavy.Count==0) { if(batch.Items>=5000) Say($"{batch.Verb} {batch.Items:N0} items ({FilePane.SizeText(batch.Bytes)}). This may take a while."); return; }
        var names=batch.Heavy.Select(h=>h.Name).ToArray();
        int inside=batch.Heavy.Sum(h=>h.Count);
        Say($"{batch.Verb} {batch.Items:N0} items, and {inside:N0} of them are inside {string.Join(", ",batch.Heavy.Select(h=>$"{h.Name} ({h.Count:N0})"))}. "+
            "Those are usually downloaded or rebuilt on the other PC (for example with npm install), so you can leave them out.");
        if(redoWithout!=null) Offer($"Stop and copy again without {string.Join(", ",names)}",()=>redoWithout(names));
    }
    /// <summary>After a batch: a short note, and for failures Ollama's explanation in plain words.</summary>
    public async Task Finished(TransferStrip.Batch batch,string message,bool failed)
    {
        Offer(null,null);
        if(!failed) { if(batch.Items>0 || message.StartsWith("Transfer stopped",StringComparison.Ordinal)) Say(message+"."); return; }
        Say(message);
        if(Model==null) return;
        try { Say(await ollama.Chat(Model,TransferAssistant.ExplainFailure(batch.Verb,message,batch.Items),null,lifetime.Token)); }
        catch(Exception e) when(e is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException or TaskCanceledException) { }
    }
}
