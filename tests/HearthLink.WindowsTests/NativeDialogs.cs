using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>Finds and answers the app's message boxes (#32770) on its UI thread.</summary>
static class NativeDialogs
{
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread,EnumWindows callback,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int size);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr dialog,int id);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    delegate bool EnumWindows(IntPtr window,IntPtr state);
    public const int Yes=6, No=7;
    public static uint CurrentThread()=>GetCurrentThreadId();
    public static List<IntPtr> Open(uint thread)
    {
        var found=new List<IntPtr>();
        EnumThreadWindows(thread,(window,_)=> { var name=new StringBuilder(64); GetClassName(window,name,name.Capacity); if(name.ToString()=="#32770") found.Add(window); return true; },IntPtr.Zero);
        return found;
    }
    /// <summary>The message text of a message box (its static control, id 0xFFFF).</summary>
    public static string Text(IntPtr dialog) { var text=new StringBuilder(2048); GetWindowText(GetDlgItem(dialog,0xFFFF),text,text.Capacity); return text.ToString(); }
    public static void Press(IntPtr dialog,int button)=>PostMessage(dialog,0x0111 /* WM_COMMAND */,button,IntPtr.Zero);
    /// <summary>Waits for one message box, presses <paramref name="button"/> and returns its text.</summary>
    public static async Task<string> Answer(uint thread,int button,TimeSpan limit)
    {
        var clock=Stopwatch.StartNew();
        while(true)
        {
            var dialogs=Open(thread);
            if(dialogs.Count>0)
            {
                // The native dialog can be enumerated before its static text control is initialized.
                string text=Text(dialogs[0]);
                if(text.Length>0) {Press(dialogs[0],button); return text;}
            }
            if(clock.Elapsed>limit) throw new TimeoutException("No message box within "+limit);
            await Task.Delay(100);
        }
    }
}
