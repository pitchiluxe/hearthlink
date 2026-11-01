namespace HearthLink.Core;

/// <summary>Only approved control sessions may request a fixed, non-forced Windows action.</summary>
public static class SessionCommands
{
    public static string? Arguments(bool control,string? command)=>control ? command switch
    {
        "restart"=>"/r /t 0",
        "shutdown"=>"/s /t 0",
        "signout"=>"/l",
        _=>null
    } : null;
}
