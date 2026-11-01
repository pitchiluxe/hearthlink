using HearthLink.Core;

static class SessionCommandChecks
{
    public static void Run(Action<bool,string> check)
    {
        string? Args(bool control,string command)=>SessionCommands.Arguments(control,command);
        check(Args(true,"restart")=="/r /t 0" && Args(true,"shutdown")=="/s /t 0" && Args(true,"signout")=="/l","remote power commands use fixed Windows arguments without forced app closure");
        check(Args(false,"restart")==null && Args(false,"shutdown")==null && Args(false,"signout")==null,"view-only sessions cannot issue power commands");
        check(Args(true,"/r /f")==null && Args(true,"ctrlaltdel")==null && Args(true,"")==null,"unknown and unsupported commands cannot execute");
    }
}
