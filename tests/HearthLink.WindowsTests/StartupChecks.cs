using HearthLink;
using Microsoft.Win32;

static class StartupChecks
{
    public static void Run(Action<bool,string> check)
    {
        check(new Settings().StartWithWindows,"new and upgraded profiles default to startup after sign-in");
        string path="Software\\HearthLinkTests\\"+Guid.NewGuid().ToString("N");
        try
        {
            using var key=Registry.CurrentUser.CreateSubKey(path);
            WindowsStartup.Apply(key,true,@"C:\Program Files\HearthLink\HearthLink.exe");
            check((string?)key.GetValue("HearthLink")=="\"C:\\Program Files\\HearthLink\\HearthLink.exe\" --startup","startup quotes executable paths and passes startup mode");
            key.SetValue("OtherApp","keep");
            WindowsStartup.Apply(key,false,@"C:\Program Files\HearthLink\HearthLink.exe");
            check(key.GetValue("HearthLink")==null && (string?)key.GetValue("OtherApp")=="keep","disabling startup removes only HearthLink registration");
        }
        finally {Registry.CurrentUser.DeleteSubKeyTree(path,false);}
    }
}
