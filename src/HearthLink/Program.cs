namespace HearthLink;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        string instance="Local\\HearthLink-"+System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        using var activate=new EventWaitHandle(false,EventResetMode.AutoReset,instance+"-activate");
        using var single=new Mutex(true,instance,out bool first);
        if(!first) {if(!args.Contains("--startup")) activate.Set(); return;}
        ApplicationConfiguration.Initialize();
        try
        {
            var profile=new IdentityStore();
            try {WindowsStartup.Apply(profile.Config.StartWithWindows);}
            catch(Exception e) when(e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {MessageBox.Show("Could not update Windows startup. Check Settings → General and your Windows startup permissions.","HearthLink");}
            finally {profile.Certificate.Dispose();}
            var window=new MainWindow();
            if(args.Contains("--startup")) window.WindowState=FormWindowState.Minimized;
            using var activationTimer=new System.Windows.Forms.Timer {Interval=400};
            activationTimer.Tick+=(_,_)=>
            {
                if(activate.WaitOne(0)) {window.Show(); window.WindowState=FormWindowState.Normal; window.Activate();}
            };
            activationTimer.Start();
            Application.Run(window);
        }
        catch(Exception e) { MessageBox.Show("HearthLink could not start: "+e.Message,"HearthLink"); }
    }
}
