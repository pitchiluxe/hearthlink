using HearthLink;
using HearthLink.Core;
using System.Reflection;

static class NavigationChecks
{
    static IEnumerable<Control> Descendants(Control root)
    {
        foreach(Control child in root.Controls)
        {
            yield return child;
            foreach(var nested in Descendants(child)) yield return nested;
        }
    }
    public static async Task Run(MainWindow owner,Action<bool,string> check)
    {
      await owner.OnUi(()=>
      {
        check(owner.FormBorderStyle==FormBorderStyle.FixedSingle && !owner.MaximizeBox &&
            owner.MinimumSize==owner.MaximumSize && !owner.MinimumSize.IsEmpty,"main app window has fixed bounds and no resize/maximize controls");
        string[] sections=["Your computers","Recent sessions","File transfers","Access & security","Connection health","Session history","Settings","Help & updates"];
        var buttons=Descendants(owner).OfType<Button>().Where(b=>b.Name.StartsWith("Nav:")).ToArray();
        check(sections.All(s=>buttons.Any(b=>b.Name=="Nav:"+s)),"all eight sidebar destinations exist");
        foreach(string section in sections)
        {
            buttons.Single(b=>b.Name=="Nav:"+section).PerformClick();
            check(owner.Text=="HearthLink • "+section,"navigation selects "+section);
        }
        buttons.Single(b=>b.Name=="Nav:Your computers").PerformClick();
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var identity=(IdentityStore)typeof(MainWindow).GetField("identity",flags)!.GetValue(owner)!;
        var list=(ListView)typeof(MainWindow).GetField("hosts",flags)!.GetValue(owner)!;
        var search=Descendants(owner).OfType<TextBox>().Single(t=>t.Name=="ComputerSearch");
        var original=identity.Config.Hosts.ToArray(); var favorites=identity.Config.Favorites.ToArray();
        string first=new('1',64),second=new('2',64);
        try
        {
            identity.Config.Hosts.Clear(); identity.Config.Hosts.AddRange([new("First PC","192.0.2.1",first),new("Second PC","192.0.2.2",second)]);
            typeof(MainWindow).GetMethod("PopulateHosts",flags)!.Invoke(owner,null);
            search.Text="Second";
            check(list.Items.Count==1 && Equals(list.Items[0].Tag,second),"computer search keeps the filtered peer fingerprint");
            list.Items[0].Selected=true;
            var selected=(Peer?)typeof(MainWindow).GetMethod("SelectedPeer",flags)!.Invoke(owner,null);
            check(selected?.Fingerprint==second,"filtered row actions resolve the selected identity rather than row index");
            typeof(MainWindow).GetMethod("ToggleFavorite",flags)!.Invoke(owner,null);
            search.Clear();
            check(Equals(list.Items[0].Tag,second) && identity.Config.Favorites.Contains(second),"favoriting reorders peers without changing their identities");
            identity.Audit(second,"outbound-connected");
            typeof(MainWindow).GetMethod("LoadRecent",flags)!.Invoke(owner,null);
            var recent=(DataGridView)typeof(MainWindow).GetField("recentGrid",flags)!.GetValue(owner)!;
            check(recent.Rows.Cast<DataGridViewRow>().Any(r=>Equals(r.Tag,second)),"recent connections read the current IdentityStore's history");
            Descendants(owner).OfType<Button>().Single(b=>b.Text=="Remove PC" && b.Visible).PerformClick();
            check(identity.Config.Hosts.Count==1 && identity.Config.Hosts[0].Fingerprint==first,"Remove PC acts on the filtered/favorited peer without removing its neighbor");
            typeof(MainWindow).GetMethod("LoadRecent",flags)!.Invoke(owner,null);
            check(recent.Rows.Cast<DataGridViewRow>().All(r=>!Equals(r.Tag,second)),"removed peers cannot be reconnected from recent sessions");
            var removedRequest=(Task)typeof(MainWindow).GetMethod("ConnectTo",flags)!.Invoke(owner,[second,false])!;
            check(removedRequest.IsFaulted && removedRequest.Exception!.GetBaseException().Message.Contains("no longer paired"),"connect revalidates current trust before attempting the network");
            identity.Config.Controller=false;
            try
            {
                var disabled=(Task)typeof(MainWindow).GetMethod("ConnectTo",flags)!.Invoke(owner,[second,false])!;
                check(disabled.IsFaulted && disabled.Exception!.GetBaseException().Message.Contains("controller"),"all connection entry points obey the disabled controller role");
            }
            finally {identity.Config.Controller=true;}
        }
        finally
        {
            identity.Config.Hosts.Clear(); identity.Config.Hosts.AddRange(original); identity.Config.Favorites=favorites.ToList(); search.Clear();
            typeof(MainWindow).GetMethod("PopulateHosts",flags)!.Invoke(owner,null); identity.Save();
        }
        var defaults=new Settings();
        check(defaults.DefaultControl && defaults.DefaultQuality==0 && defaults.Favorites.Count==0,"older settings retain compatible default session preferences");
        check(!defaults.Ai.CloudFallback && defaults.Ai.PerformanceAgent && defaults.Ai.MinimumQuality==0 && defaults.Ai.MaximumQuality==2,"AI defaults keep cloud fallback off and automatic quality in the approved range");
        var aiTabs=Descendants(owner).OfType<TabControl>().Single(t=>t.Name=="SettingsTabs");
        check(aiTabs.TabPages.Count==2 && Descendants(owner).OfType<TextBox>().Where(t=>t.Name is "OpenAIKey" or "ClaudeKey").All(t=>t.UseSystemPasswordChar),"Settings includes AI provider controls with masked API key fields");
        string folder=Path.Combine(Path.GetTempPath(),"HearthLink-navigation-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new IdentityStore(folder); store.Config.DefaultControl=false; store.Config.DefaultQuality=3; store.Config.Favorites.Add(second); store.Save();
            store.Config.Ai=new() {CloudFallback=true,OpenAIKey="fixture-openai-key",OpenAIModel="gpt-fixture",ClaudeKey="fixture-claude-key",ClaudeModel="claude-fixture",MinimumQuality=1,MaximumQuality=3}; store.Save();
            var loaded=new IdentityStore(folder);
            check(!loaded.Config.DefaultControl && loaded.Config.DefaultQuality==3 && loaded.Config.Favorites.Contains(second),"new preferences survive protected storage roundtrip");
            check(loaded.Config.Ai.CloudFallback && loaded.Config.Ai.OpenAIKey=="fixture-openai-key" && loaded.Config.Ai.MinimumQuality==1 &&
                !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder,"settings.protected"))).Contains("fixture-openai-key") && !loaded.Config.Ai.ToString().Contains("fixture"),"AI keys and preferences roundtrip through DPAPI without plaintext credential storage or diagnostic rendering");
            using(var runtime=new AiRuntime(()=>store.Config.Ai))
            using(var panel=new AiSettingsPanel(store,runtime))
            {
                string protectedPath=Path.Combine(folder,"settings.protected");
                var prior=store.Config.Ai;
                var key=Descendants(panel).OfType<TextBox>().Single(t=>t.Name=="OpenAIKey"); key.Text="fixture-changed";
                bool escaped=false;
                try
                {
                    File.SetAttributes(protectedPath,FileAttributes.ReadOnly);
                    try {typeof(AiSettingsPanel).GetMethod("Save",flags)!.Invoke(panel,null);}
                    catch(TargetInvocationException) {escaped=true;}
                }
                finally {File.SetAttributes(protectedPath,FileAttributes.Normal);}
                check(!escaped && ReferenceEquals(store.Config.Ai,prior),"denied AI-settings writes are handled in the UI and preserve previous credentials");
            }
            File.WriteAllText(Path.Combine(folder,"history.jsonl"),"broken\nnull\n");
            for(int i=0;i<1100;i++) store.Audit(second,"outbound-connected");
            File.AppendAllText(Path.Combine(folder,"history.jsonl"),"{truncated");
            check(store.ReadHistory().Count==1000,"history skips damaged lines and caps the returned tail at 1000 records");
            File.WriteAllText(Path.Combine(folder,"history.jsonl"),new string('x',600_000)+"\n"); store.Audit(second,"connected");
            check(store.ReadHistory() is {Count:1} tail && tail[0].Outcome=="connected","large history reads a bounded tail and discards the first partial line");
            store.Certificate.Dispose(); loaded.Certificate.Dispose();
        }
        finally {Directory.Delete(folder,true);}
        return true;
      });
      var activities=new List<TransferActivity>();
      var strip=await owner.OnUi(()=>
      {
          var item=new TransferStrip(_=>Task.FromException<Connection>(new IOException("fixture preparation failed")));
          item.ActivityChanged+=activities.Add;
          typeof(MainWindow).GetMethod("ObserveTransfer",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owner,[item,"Fixture PC"]);
          return item;
      });
      await owner.OnUi(()=>strip.Send(Array.Empty<string>())).Unwrap();
      await owner.OnUi(()=>
      {
          check(activities.Select(a=>a.State).SequenceEqual(["Preparing","Failed"]),"failed transfer preparation emits a terminal activity with a stable ID");
          check(activities.Select(a=>a.Id).Distinct().Count()==1,"transfer lifecycle retains a single activity ID");
          strip.Dispose(); return true;
      });
      activities.Clear();
      var waiting=await owner.OnUi(()=>
      {
          var item=new TransferStrip(async token=> {await Task.Delay(Timeout.Infinite,token); throw new IOException();});
          item.ActivityChanged+=activities.Add; return item;
      });
      var transfer=await owner.OnUi(()=>
      {
          var task=waiting.Send(Array.Empty<string>()); waiting.Cancel(); return task;
      });
      await transfer;
      await owner.OnUi(()=>
      {
          check(activities.Select(a=>a.State).SequenceEqual(["Preparing","Cancelled"]),"cancelling transfer preparation emits Cancelled rather than Failed");
          waiting.Dispose(); return true;
      });
    }
}
