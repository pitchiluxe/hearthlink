using System.Text.Json;

namespace HearthLink.Core;
public sealed record PerformanceSample(double FramesPerSecond,int RoundTripMilliseconds,double HostMilliseconds,int Profile,int FreshFrames);
/// <summary>Numeric image-quality decisions only. Has no access to files, input or authorization.</summary>
public sealed class PerformancePolicy
{
    int congested;
    DateTimeOffset lastAttempt=DateTimeOffset.MinValue;
    public bool Observe(PerformanceSample sample,DateTimeOffset now)
    {
        if(sample.FreshFrames<=0 || sample.RoundTripMilliseconds<0 || !double.IsFinite(sample.HostMilliseconds)) {congested=0; return false;}
        bool slow=sample.RoundTripMilliseconds>=180 || sample.HostMilliseconds>=100;
        congested=slow?congested+1:0;
        if(congested<3 || now-lastAttempt<TimeSpan.FromMinutes(2)) return false;
        lastAttempt=now; congested=0; return true;
    }
    public static int? ParseProfile(string text,int current,int minimum,int maximum)
    {
        if(text.Length>2048 || minimum<0 || maximum>4 || minimum>maximum || current<minimum || current>maximum) return null;
        try
        {
            using var json=JsonDocument.Parse(text);
            if(json.RootElement.ValueKind!=JsonValueKind.Object || !json.RootElement.TryGetProperty("profile",out var value) || value.ValueKind!=JsonValueKind.Number || !value.TryGetInt32(out int profile) || profile is <0 or >4) return null;
            int target=Math.Clamp(profile,minimum,maximum);
            // Congestion permits reductions only; never silently raise quality or jump multiple profiles.
            return target>=current?current:Math.Max(current-1,target);
        }
        catch(JsonException) {return null;}
    }
    public static IReadOnlyList<AiMessage> Messages(PerformanceSample sample,int minimum,int maximum)=>
    [
        new("system","You advise image quality for a remote desktop session. Return only JSON {\"profile\":integer}. Profiles: 0 Responsive, 1 Balanced, 2 Full detail, 3 Sharp, 4 Best. Sustained congestion was measured. Suggest reducing by one step or keeping the current profile; never increase it. You have no tools or other actions."),
        new("user",JsonSerializer.Serialize(new {fps=sample.FramesPerSecond,rttMs=sample.RoundTripMilliseconds,hostMs=sample.HostMilliseconds,currentProfile=sample.Profile,minimum,maximum}))
    ];
}
/// <summary>Rejects recommendations that became stale while a local model was thinking.</summary>
public sealed class PerformanceAgent(Func<AiOptions> options,Func<PerformanceSample,AiOptions,CancellationToken,Task<string?>> infer)
{
    readonly PerformancePolicy policy=new();
    bool paused;
    public void ManualOverride()=>paused=true;
    public async Task<int?> Evaluate(PerformanceSample sample,DateTimeOffset now,Func<int> currentProfile,Func<bool> active,CancellationToken token)
    {
        var config=options();
        if(paused || !config.PerformanceAgent || config.MinimumQuality<0 || config.MaximumQuality>4 || config.MinimumQuality>config.MaximumQuality ||
            !active() || sample.Profile<=config.MinimumQuality || sample.Profile>config.MaximumQuality ||
            !policy.Observe(sample,now)) return null;
        var started=DateTimeOffset.UtcNow;
        string? answer=await infer(sample,config,token);
        token.ThrowIfCancellationRequested();
        if(answer==null || paused || !active() || !ReferenceEquals(config,options()) || currentProfile()!=sample.Profile || DateTimeOffset.UtcNow-started>TimeSpan.FromSeconds(10)) return null;
        return PerformancePolicy.ParseProfile(answer,sample.Profile,config.MinimumQuality,config.MaximumQuality);
    }
}
