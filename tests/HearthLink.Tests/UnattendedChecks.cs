using HearthLink.Core;

static class UnattendedChecks
{
    public static void Run(Action<bool,string> check)
    {
        var paired=new TrustBook(); paired.Add("AB12","Desk");
        check(!UnattendedAccess.SkipsApproval(false,paired,"AB12"),"unattended off: a paired controller still gets the Allow dialog");
        check(!UnattendedAccess.SkipsApproval(true,paired,"CD34") && !UnattendedAccess.SkipsApproval(true,paired,""),"unattended on: an unpaired fingerprint never skips approval");
        check(UnattendedAccess.SkipsApproval(true,paired,"AB12"),"unattended on: an exact paired fingerprint skips approval");
        check(!UnattendedAccess.SkipsApproval(true,paired,"ab12"),"unattended match is exact, not case-folded");
        paired.Remove("AB12");
        check(!UnattendedAccess.SkipsApproval(true,paired,"AB12"),"revoked controller no longer skips approval");
        check(UnattendedAccess.IdleLimit==TimeSpan.FromMinutes(30),"unattended idle limit is 30 minutes");
        var start=DateTimeOffset.UtcNow; var idle=new IdleClock(UnattendedAccess.IdleLimit,start);
        check(!idle.Expired(start.AddMinutes(29)) && idle.Expired(start.AddMinutes(30)),"idle clock expires at the limit, not before");
        idle.Touch(start.AddMinutes(20));
        check(!idle.Expired(start.AddMinutes(49)) && idle.Expired(start.AddMinutes(50)),"controller input restarts the idle clock");
        check(idle.CheckEvery==TimeSpan.FromSeconds(30) && new IdleClock(TimeSpan.FromSeconds(1),start).CheckEvery==TimeSpan.FromMilliseconds(100),"idle checks run every 30 s for 30 minutes and never under 100 ms");
        check(new[] {"key","move","button","wheel","release","monitor","profile"}.All(UnattendedAccess.IsActivity) && !UnattendedAccess.IsActivity("presented") && !UnattendedAccess.IsActivity("disconnect"),"only deliberate controller input counts as activity, not frame acknowledgements");
    }
}
