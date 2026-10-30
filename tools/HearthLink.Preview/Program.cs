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
            foreach(var size in new[]{window.ClientSize})
            {
                window.ClientSize=size; window.PerformLayout(); Application.DoEvents();
                using var image=new Bitmap(window.Width,window.Height);
                window.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                string path=Path.Combine(output,$"HearthLink-{size.Width}.png"); image.Save(path,ImageFormat.Png); Console.WriteLine(path);
            }
            foreach(string section in new[]{"Recent sessions","File transfers","Access & security","Connection health","Session history","Settings","Help & updates"})
            {
                typeof(MainWindow).GetMethod("Navigate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,[section]);
                Application.DoEvents();
                using var image=new Bitmap(window.Width,window.Height);
                window.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                string path=Path.Combine(output,"HearthLink-"+section.Replace(' ','-').Replace('&','a')+".png");
                image.Save(path,ImageFormat.Png); Console.WriteLine(path);
            }
            typeof(MainWindow).GetMethod("Navigate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,["Settings"]);
            var aiTabs=window.Controls.Find("SettingsTabs",true).OfType<TabControl>().Single();
            aiTabs.SelectedIndex=1; Application.DoEvents();
            using(var aiImage=new Bitmap(window.Width,window.Height))
            {
                window.DrawToBitmap(aiImage,new Rectangle(Point.Empty,aiImage.Size));
                aiImage.Save(Path.Combine(output,"HearthLink-AI-settings.png"),ImageFormat.Png);
            }
            aiTabs.SelectedIndex=0;
            Button? FindUpdates(Control root)
            {
                foreach(Control child in root.Controls)
                {if(child is Button button && button.Text=="App updates") return button; if(FindUpdates(child) is Button nested) return nested;}
                return null;
            }
            typeof(MainWindow).GetMethod("Navigate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,["Your computers"]);
            FindUpdates(window)!.PerformClick(); Application.DoEvents();
            using(var updates=window.OwnedForms.Single(f=>f.Text=="HearthLink • Updates"))
            {
                using var image=new Bitmap(updates.Width,updates.Height);
                updates.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                string path=Path.Combine(output,"HearthLink-updates.png"); image.Save(path,ImageFormat.Png); Console.WriteLine(path);
            }
            // Scaled-layout checks supplement native DPI acceptance; this does not change Windows display settings.
            var baseClient=window.ClientSize;
            // The product is fixed-size; only this isolated preview harness releases its bounds for simulated scaling.
            window.MinimumSize=Size.Empty; window.MaximumSize=Size.Empty;
            float previousScale=1;
            foreach(float scale in new[]{1.25f,2f})
            {
                window.Scale(new SizeF(scale/previousScale,scale/previousScale)); previousScale=scale;
                window.ClientSize=new((int)(baseClient.Width*scale),(int)(baseClient.Height*scale));
                foreach(string section in new[]{"Your computers","Access & security","Settings"})
                {
                    typeof(MainWindow).GetMethod("Navigate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,[section]);
                    Application.DoEvents();
                    using var image=new Bitmap(window.Width,window.Height);
                    window.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                    string path=Path.Combine(output,$"HearthLink-{section.Replace(' ','-').Replace('&','a')}-scaled-{scale*100:0}.png");
                    image.Save(path,ImageFormat.Png); Console.WriteLine(path);
                }
            }
            window.Close();
        }
        finally { if(Directory.Exists(fixture)) Directory.Delete(fixture,true); }
    }
}
