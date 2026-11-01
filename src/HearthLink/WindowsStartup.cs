using Microsoft.Win32;

namespace HearthLink;
public static class WindowsStartup
{
    public static void Apply(RegistryKey key,bool enabled,string executable)
    {
        if(enabled) key.SetValue("HearthLink","\""+executable+"\" --startup",RegistryValueKind.String);
        else key.DeleteValue("HearthLink",false);
    }
    public static void Apply(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        Apply(key,enabled,Path.Combine(AppContext.BaseDirectory,"HearthLink.exe"));
    }
}
