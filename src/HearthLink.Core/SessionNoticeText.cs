namespace HearthLink.Core;
public static class SessionNoticeText
{
    public static string Controller(string name)=>PeerName.Clean(name);
    public static string Message(string name,bool control)=>$"{Controller(name)} is connected\n{(control?"Keyboard and mouse control":"View only")}";
}
