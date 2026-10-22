using System.Threading.Channels;
namespace HearthLink.Core;
/// <summary>Coalesce motion, preserve key/button transitions, and bound memory under congestion.</summary>
public sealed class InputMailbox
{
    readonly object sync=new();
    readonly Queue<Packet> ordered=new();
    readonly Channel<byte> signal=Channel.CreateBounded<byte>(new BoundedChannelOptions(1) {SingleReader=true,FullMode=BoundedChannelFullMode.DropWrite});
    readonly int capacity;
    Packet? motion;
    public InputMailbox(int capacity=128) { if(capacity<2) throw new ArgumentOutOfRangeException(nameof(capacity)); this.capacity=capacity; }
    public bool Post(Packet packet)
    {
        lock(sync)
        {
            if(packet.Kind=="move") motion=packet;
            else
            {
                if(ordered.Count+1+(motion==null?0:1)>capacity) return false;
                if(motion!=null) {ordered.Enqueue(motion); motion=null;}
                ordered.Enqueue(packet);
            }
        }
        signal.Writer.TryWrite(1); return true;
    }
    public async ValueTask<Packet> ReadAsync(CancellationToken token=default)
    {
        while(true)
        {
            lock(sync)
            {
                if(ordered.Count>0) return ordered.Dequeue();
                if(motion!=null) {var latest=motion; motion=null; return latest;}
            }
            await signal.Reader.ReadAsync(token).ConfigureAwait(false);
        }
    }
}
