namespace HearthLink.Core;
/// <summary>
/// Rules for unattended access. Only the host's own switch (changed after a Windows Hello check at the host) and the
/// peer's pinned certificate fingerprint decide; nothing the peer sends, such as its name or text, is consulted.
/// </summary>
public static class UnattendedAccess
{
    public static readonly TimeSpan IdleLimit=TimeSpan.FromMinutes(30);
    // History outcomes.
    public const string Connected="unattended-connected", IdleDisconnected="unattended-idle-disconnected", Enabled="unattended-enabled", Disabled="unattended-disabled", ChangeRefused="unattended-change-refused";
    /// <summary>True only when the switch is on and <paramref name="fingerprint"/> is an exact paired controller.</summary>
    public static bool SkipsApproval(bool switchOn,TrustBook controllers,string fingerprint)=>switchOn && fingerprint.Length>0 && controllers.Contains(fingerprint);
    /// <summary>Packets a person at the controller sends on purpose. Frame acknowledgements are automatic and never keep a session alive.</summary>
    public static bool IsActivity(string kind)=>kind is "key" or "move" or "button" or "wheel" or "release" or "monitor" or "profile";
}
/// <summary>Tracks the last controller input of a session; thread-safe.</summary>
public sealed class IdleClock
{
    long last;
    public TimeSpan Limit { get; }
    public IdleClock(TimeSpan limit,DateTimeOffset now) { Limit=limit; last=now.UtcTicks; }
    public void Touch(DateTimeOffset now)=>Interlocked.Exchange(ref last,now.UtcTicks);
    public bool Expired(DateTimeOffset now)=>now.UtcTicks-Interlocked.Read(ref last)>=Limit.Ticks;
    /// <summary>How often to look: often enough to end close to the limit, never busier than every 100 ms.</summary>
    public TimeSpan CheckEvery=>TimeSpan.FromTicks(Math.Clamp(Limit.Ticks/10,TimeSpan.FromMilliseconds(100).Ticks,TimeSpan.FromSeconds(30).Ticks));
}
