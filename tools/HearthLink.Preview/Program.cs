using HearthLink;
using System.Drawing.Imaging;

internal static class Preview
{
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string output=Path.GetFullPath(args.Length>0?args[0]:"artifacts/design"); Directory.CreateDirectory(output);
        string fixture=Path.Combine(Path.GetTempPath(),"HearthLink-preview-"+Guid.NewGuid().ToString("N"));
        try
        {
            var identity=new IdentityStore(fixture); identity.Config.Name="This PC";
            using var window=new MainWindow(identity) {ShowInTaskbar=false,Opacity=0};
            // Render the application's own controls. No screen capture, network hosting,
            // simulated peer or production identity is used by this preview tool.
            window.Show(); Application.DoEvents();
            foreach(var size in new[]{new Size(1340,880),new Size(1120,790)})
            {
                window.ClientSize=size; window.PerformLayout(); Application.DoEvents();
                using var image=new Bitmap(window.Width,window.Height);
                window.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                string path=Path.Combine(output,$"HearthLink-{size.Width}.png"); image.Save(path,ImageFormat.Png); Console.WriteLine(path);
            }
            Button? FindUpdates(Control root)
            {
                foreach(Control child in root.Controls)
                {if(child is Button button && button.Text=="App updates") return button; if(FindUpdates(child) is Button nested) return nested;}
                return null;
            }
            FindUpdates(window)!.PerformClick(); Application.DoEvents();
            using(var updates=window.OwnedForms.Single(f=>f.Text=="HearthLink • Updates"))
            {
                using var image=new Bitmap(updates.Width,updates.Height);
                updates.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                string path=Path.Combine(output,"HearthLink-updates.png"); image.Save(path,ImageFormat.Png); Console.WriteLine(path);
            }
            window.Close();
        }
        finally { if(Directory.Exists(fixture)) Directory.Delete(fixture,true); }
    }
}
