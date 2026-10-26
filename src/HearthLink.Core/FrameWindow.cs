using System.Diagnostics;
namespace HearthLink.Core;
/// <summary>
/// At most <see cref="Depth"/> frames await presentation: enough to keep frames flowing across a round trip, too few for a
/// slow viewer to build a backlog. The viewer paints only the newest frame, so an acknowledgement covers every earlier one.
/// </summary>
public sealed class FrameWindow : IDisposable
{
    public const int Depth=3;
    readonly SemaphoreSlim turn=new(Depth,Depth);
    readonly object sync=new();
    // Sequence → send timestamp, 0 while the frame is acquired but not yet sent.
    readonly SortedDictionary<long,long> open=new();
    long next;
    int roundTrip;
    public int RoundTripMilliseconds { get { lock(sync) return roundTrip; } }
    public async Task<long> AcquireAsync(CancellationToken token=default)
    {
        if(!await turn.WaitAsync(TimeSpan.FromSeconds(8),token).ConfigureAwait(false)) throw new TimeoutException("Viewer stopped presenting frames.");
        lock(sync) { open[++next]=0; return next; }
    }
    public void MarkSent(long sequence) { lock(sync) { if(!open.ContainsKey(sequence)) throw new InvalidOperationException("Unexpected frame sequence."); open[sequence]=Stopwatch.GetTimestamp(); } }
    /// <summary>Gives back the slot of a frame that will not be sent, such as an unchanged screen.</summary>
    public void Abandon(long sequence) { lock(sync) { if(open.Remove(sequence)) turn.Release(); } }
    public bool Acknowledge(long sequence)
    {
        lock(sync)
        {
            if(!open.TryGetValue(sequence,out long sentAt) || sentAt==0) return false;
            roundTrip=(int)Math.Clamp(Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds,0,60000);
            var covered=open.Keys.TakeWhile(s=>s<=sequence).ToArray();
            foreach(long s in covered) open.Remove(s);
            turn.Release(covered.Length); return true;
        }
    }
    public void Dispose()=>turn.Dispose();
}
