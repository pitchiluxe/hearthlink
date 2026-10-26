using Windows.Security.Credentials.UI;

namespace HearthLink;
/// <summary>
/// The outcome of asking the person at this PC to prove they are here. <see cref="Problem"/> explains a failure to them;
/// <see cref="Code"/> is the raw Windows answer (for example "availability:NotConfiguredForUser"), kept in history so a refusal can be diagnosed.
/// </summary>
public sealed record PresenceCheck(bool Verified,string? Problem=null,string? Code=null);
/// <summary>Windows Hello (PIN, face or fingerprint) at this PC, through <see cref="UserConsentVerifier"/>.</summary>
public static class WindowsHello
{
    const string Unchanged=" Unattended access stays as it was.";
    const string AddPin="Open Settings > Accounts > Sign-in options, set up a PIN (Windows Hello), then try again.";
    public const string SetUpHint="Windows Hello is not set up for your Windows account on this PC (no PIN, face or fingerprint).\n\n"+AddPin+Unchanged;
    public static async Task<PresenceCheck> Verify(IntPtr owner,string reason)
    {
        UserConsentVerifierAvailability availability;
        try { availability=await UserConsentVerifier.CheckAvailabilityAsync(); }
        catch(Exception e) { return Failure("availability",e); }
        if(availability!=UserConsentVerifierAvailability.Available) return new(false,Unavailable(availability),"availability:"+availability);
        UserConsentVerificationResult result;
        try { result=await UserConsentVerifierInterop.RequestVerificationForWindowAsync(owner,reason); }
        catch(Exception e) { return Failure("verification",e); }
        return result==UserConsentVerificationResult.Verified ? new(true,null,"verified") : new(false,Failed(result),"result:"+result);
    }
    static PresenceCheck Failure(string step,Exception e)=>new(false,$"Windows Hello could not start (error 0x{e.HResult:X8} during {step}).{Unchanged}",$"{step}-error:0x{e.HResult:X8}");
    public static string Unavailable(UserConsentVerifierAvailability availability)=>availability switch
    {
        UserConsentVerifierAvailability.NotConfiguredForUser=>SetUpHint,
        UserConsentVerifierAvailability.DeviceNotPresent=>"Windows reports no Windows Hello sign-in (PIN, face or fingerprint) on this PC.\n\n"+AddPin+Unchanged,
        UserConsentVerifierAvailability.DisabledByPolicy=>"Windows Hello is turned off by a policy on this PC (for example by an organization), so unattended access cannot be changed here.",
        UserConsentVerifierAvailability.DeviceBusy=>"Windows Hello is busy with another request. Try again in a moment."+Unchanged,
        _=>$"Windows Hello is not available ({availability})."+Unchanged
    };
    /// <summary>Null when the person cancelled: nothing to explain.</summary>
    public static string? Failed(UserConsentVerificationResult result)=>result switch
    {
        UserConsentVerificationResult.Canceled=>null,
        UserConsentVerificationResult.RetriesExhausted=>"Too many Windows Hello attempts."+Unchanged,
        UserConsentVerificationResult.DeviceBusy=>"Windows Hello is busy with another request. Try again in a moment."+Unchanged,
        UserConsentVerificationResult.DisabledByPolicy=>"Windows Hello is turned off by a policy on this PC, so unattended access cannot be changed here.",
        // Availability said Hello is ready, then the prompt said otherwise: report it as such, not as "not set up".
        UserConsentVerificationResult.DeviceNotPresent or UserConsentVerificationResult.NotConfiguredForUser=>$"Windows Hello said it was ready, then refused the check ({result}). Try again; if this repeats, check your PIN under Settings > Accounts > Sign-in options."+Unchanged,
        _=>$"Windows Hello could not verify you ({result})."+Unchanged
    };
}
