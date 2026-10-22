using HearthLink;
using System.Drawing.Imaging;
using System.Reflection;

internal static class ManualScreens
{
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string output=Path.GetFullPath(args.Length>0?args[0]:"artifacts/manual/images"); Directory.CreateDirectory(output);
        string fixture=Path.Combine(Path.GetTempPath(),"HearthLink-manual-"+Guid.NewGuid().ToString("N"));
        try
        {
            var identity=new IdentityStore(fixture); identity.Config.Name="This PC";
            using var window=new MainWindow(identity) {ShowInTaskbar=false,Opacity=0};
            window.Show(); Application.DoEvents();
            using(var image=new Bitmap(window.Width,window.Height))
            { window.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(Path.Combine(output,"dashboard.png"),ImageFormat.Png); }
            // Capture only the actual pairing-entry dialog, then cancel it.
            // No pairing request, peer, approval, screen stream or online state is fabricated.
            using var timer=new System.Windows.Forms.Timer {Interval=200};
            timer.Tick+=(_,_)=>
            {
                var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Text=="HearthLink pairing");
                if(dialog==null) return;
                timer.Stop(); dialog.Opacity=0; dialog.PerformLayout();
                using var image=new Bitmap(dialog.Width,dialog.Height);
                dialog.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                image.Save(Path.Combine(output,"pairing-entry.png"),ImageFormat.Png);
                dialog.DialogResult=DialogResult.Cancel;
            };
            timer.Start();
            typeof(MainWindow).GetMethod("Prompt",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,["Enter the one-time code shown on the remote PC"]);
            window.Close();
            Console.WriteLine("Rendered dashboard and actual pairing-entry dialog; no remote session tested.");
        }
        finally { if(Directory.Exists(fixture)) Directory.Delete(fixture,true); }
    }
}
