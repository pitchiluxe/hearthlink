namespace HearthLink;
internal static class Program
{
    [STAThread] static void Main()
    {
        ApplicationConfiguration.Initialize();
        try { Application.Run(new MainWindow()); }
        catch(Exception e) { MessageBox.Show("HearthLink could not start: "+e.Message,"HearthLink"); }
    }
}
