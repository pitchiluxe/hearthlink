using HearthLink;
using HearthLink.Core;

// Unattended access on the real host and its UI thread. Windows Hello is replaced by a stub so no prompt appears.
static class UnattendedChecks
{
    const int Ok=1;
    public static async Task Run(MainWindow window,HostServer server,IdentityStore host,IdentityStore controller,IdentityStore stranger,string root,Action<bool,string> check)
    {
        uint uiThread=await window.OnUi(NativeDialogs.CurrentThread);
        string history=Path.Combine(root,"host","history.jsonl");
        int Count(string outcome)=>File.Exists(history)?File.ReadLines(history).Count(l=>l.Contains($"\"outcome\":\"{outcome}\"")):0;
        bool Saved()=>new IdentityStore(Path.Combine(root,"host")).Config.Unattended;
        var asked=new List<string>(); PresenceCheck answer=new(false);
        await window.OnUi(()=> { window.VerifyPresence=reason=> { asked.Add(reason); return Task.FromResult(answer); }; return true; });
        Task<bool> Change(bool on)=>window.OnUi(()=>window.ChangeUnattended(on)).Unwrap();

        check(!new IdentityStore(Path.Combine(root,"unattended-default")).Config.Unattended && !host.Config.Unattended,"unattended access is off by default");
        bool changed=await Change(true);
        check(!changed && asked.Count==1 && !host.Config.Unattended && !Saved() && Count(UnattendedAccess.ChangeRefused)==1,"cancelled Windows Hello leaves unattended access off");
        answer=new(false,WindowsHello.SetUpHint);
        var refused=Change(true);
        string explained=await NativeDialogs.Answer(uiThread,Ok,TimeSpan.FromSeconds(15));
        check(!await refused && explained.Contains("Sign-in options") && !host.Config.Unattended,"without Windows Hello set up, the host explains how to add a PIN and stays attended");

        using(var c=await Connection.Connect("127.0.0.1",controller,host.Fingerprint))
        {
            await c.Send(new("session",Control:true,Value:Wire.ProtocolVersion));
            string prompt=await NativeDialogs.Answer(uiThread,NativeDialogs.No,TimeSpan.FromSeconds(15));
            check(prompt.Contains("access requested") && (await c.Read()).Kind=="denied","unattended off: a paired controller still needs the Allow dialog");
        }
        await Task.Delay(300);

        answer=new(true);
        changed=await Change(true);
        check(changed && asked.Count==3 && host.Config.Unattended && Saved() && Count(UnattendedAccess.Enabled)==1,"verified Windows Hello turns unattended access on and saves it");

        server.UnattendedIdleLimit=TimeSpan.FromSeconds(1.5);
        int connectedBefore=Count(UnattendedAccess.Connected);
        using(var c=await Connection.Connect("127.0.0.1",stranger,host.Fingerprint))
        {
            await c.Send(new("session",Control:true,Value:Wire.ProtocolVersion));
            check((await c.Read()).Kind=="denied" && NativeDialogs.Open(uiThread).Count==0 && Count(UnattendedAccess.Connected)==connectedBefore,"unattended on: an unpaired PC is still denied");
        }
        {
            var c=await Connection.Connect("127.0.0.1",controller,host.Fingerprint);
            await c.Send(new("session",Control:true,Value:Wire.ProtocolVersion));
            var reading=c.Read();
            // A regression would show the Allow dialog; decline it so the fixture can still shut down.
            bool prompted=await Task.WhenAny(reading,Task.Delay(TimeSpan.FromSeconds(8)))!=reading;
            if(prompted) foreach(var dialog in NativeDialogs.Open(uiThread)) NativeDialogs.Press(dialog,NativeDialogs.No);
            var ready=await reading;
            bool banner=await window.OnUi(()=>window.OwnedForms.OfType<SessionNotice>().Any(n=>n.Visible));
            check(!prompted && ready.Kind=="ready" && server.HasActiveSession && banner && Count(UnattendedAccess.Connected)==connectedBefore+1,"unattended on: a paired controller connects without the dialog, with the banner shown and unattended-connected in history");
            var reader=Task.Run(async()=>
            {
                try { while(true) { var p=await c.Read(); if(p.Frame is {} f) await c.Send(new("presented",Sequence:f.Sequence)); } }
                catch(Exception e) when(e is IOException or ObjectDisposedException or InvalidOperationException or InvalidDataException) { }
            });
            for(int i=0;i<10;i++) { await c.Send(new("monitor",Value:0)); await Task.Delay(300); }
            check(server.HasActiveSession && !reader.IsCompleted,"controller input keeps an unattended session open past the idle limit");
            bool ended=await Task.WhenAny(reader,Task.Delay(TimeSpan.FromSeconds(10)))==reader;
            c.Dispose(); await reader;
            bool bannerGone=await window.OnUi(()=>!window.OwnedForms.OfType<SessionNotice>().Any(n=>n.Visible));
            check(ended && !server.HasActiveSession && bannerGone && Count(UnattendedAccess.IdleDisconnected)==1,"an unattended session without input disconnects at the idle limit and records it");
        }
        server.UnattendedIdleLimit=UnattendedAccess.IdleLimit;
        await Task.Delay(300);

        answer=new(false);
        changed=await Change(false);
        check(!changed && host.Config.Unattended && Saved(),"turning unattended access off also needs Windows Hello");
        answer=new(true);
        changed=await Change(false);
        check(changed && !host.Config.Unattended && !Saved() && Count(UnattendedAccess.Disabled)==1,"verified Windows Hello turns unattended access off");
        using(var c=await Connection.Connect("127.0.0.1",controller,host.Fingerprint))
        {
            await c.Send(new("session",Control:true,Value:Wire.ProtocolVersion));
            string prompt=await NativeDialogs.Answer(uiThread,NativeDialogs.No,TimeSpan.FromSeconds(15));
            check(prompt.Contains("access requested") && (await c.Read()).Kind=="denied","after turning it off, sessions need the Allow dialog again");
        }
        await window.OnUi(()=> { window.VerifyPresence=_=>Task.FromResult(new PresenceCheck(false)); return true; });
        await Task.Delay(300);
    }
}
