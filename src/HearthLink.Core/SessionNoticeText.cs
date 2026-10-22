namespace HearthLink.Core;
public static class SessionNoticeText
{
    public static string Controller(string name)=>new string(name.Where(c=>!char.IsControl(c)).Take(64).ToArray());
    public static string Message(string name,bool control)=>$"{Controller(name)} is connected\n{(control?"Keyboard and mouse control":"View only")}";
}
