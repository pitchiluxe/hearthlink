using System.Diagnostics;
namespace HearthLink.Core;
/// <summary>Only one frame may await presentation; a slow peer cannot build a video backlog.</summary>
public sealed class FrameWindow : IDisposable
{
    readonly SemaphoreSlim turn=new(1,1);
    readonly object sync=new();
    long next,pending,sentAt;
    int roundTrip;
    public int RoundTripMilliseconds { get { lock(sync) return roundTrip; } }
    public async Task<long> AcquireAsync(CancellationToken token=default)
    {
        if(!await turn.WaitAsync(TimeSpan.FromSeconds(8),token).ConfigureAwait(false)) throw new TimeoutException("Viewer stopped presenting frames.");
        lock(sync) {pending=++next; sentAt=0; return pending;}
    }
    public void MarkSent(long sequence) {lock(sync) { if(pending!=sequence) throw new InvalidOperationException("Unexpected frame sequence."); sentAt=Stopwatch.GetTimestamp(); }}
    public bool Acknowledge(long sequence)
    {
        lock(sync)
        {
            if(sequence!=pending || sentAt==0) return false;
            roundTrip=(int)Math.Clamp(Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds,0,60000);
            pending=0; sentAt=0; turn.Release(); return true;
        }
    }
    public void Dispose()=>turn.Dispose();
}
