using Windows.Security.Credentials.UI;

namespace HearthLink;
/// <summary>The outcome of asking the person at this PC to prove they are here. <see cref="Problem"/> explains a failure to them.</summary>
public sealed record PresenceCheck(bool Verified,string? Problem=null);
/// <summary>Windows Hello (PIN, face or fingerprint) at this PC, through <see cref="UserConsentVerifier"/>.</summary>
public static class WindowsHello
{
    public const string SetUpHint="Windows Hello is not set up for your account on this PC.\n\nOpen Settings > Accounts > Sign-in options, add a PIN (Windows Hello), then try again. Unattended access stays as it was.";
    public static async Task<PresenceCheck> Verify(IntPtr owner,string reason)
    {
        var availability=await UserConsentVerifier.CheckAvailabilityAsync();
        if(availability!=UserConsentVerifierAvailability.Available) return new(false,Unavailable(availability));
        var result=await UserConsentVerifierInterop.RequestVerificationForWindowAsync(owner,reason);
        return result==UserConsentVerificationResult.Verified ? new(true) : new(false,Failed(result));
    }
    public static string Unavailable(UserConsentVerifierAvailability availability)=>availability switch
    {
        UserConsentVerifierAvailability.DisabledByPolicy=>"Windows Hello is turned off by policy on this PC, so unattended access cannot be changed.",
        UserConsentVerifierAvailability.DeviceBusy=>"Windows Hello is busy. Try again in a moment.",
        _=>SetUpHint
    };
    public static string? Failed(UserConsentVerificationResult result)=>result switch
    {
        UserConsentVerificationResult.Canceled=>null,
        UserConsentVerificationResult.RetriesExhausted=>"Too many Windows Hello attempts. Unattended access was not changed.",
        UserConsentVerificationResult.DeviceNotPresent or UserConsentVerificationResult.NotConfiguredForUser=>SetUpHint,
        UserConsentVerificationResult.DisabledByPolicy=>"Windows Hello is turned off by policy on this PC, so unattended access cannot be changed.",
        _=>"Windows Hello could not verify you. Unattended access was not changed."
    };
}
