using System.Net.WebSockets;
namespace HearthLink.Core;
public sealed class RelayStream(WebSocket socket) : Stream
{
    int messageBytes;
    bool closed;
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    // SslStream writes its handshake-failure alert synchronously; everything else uses WriteAsync.
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if(buffer.IsEmpty || closed) return 0;
        while(true)
        {
            ValueWebSocketReceiveResult result;
            try { result=await socket.ReceiveAsync(buffer,token).ConfigureAwait(false); }
            catch(WebSocketException ex) when(ex.WebSocketErrorCode==WebSocketError.ConnectionClosedPrematurely || socket.State is WebSocketState.Aborted or WebSocketState.Closed) { closed=true; return 0; }
            if(result.MessageType==WebSocketMessageType.Close) { closed=true; return 0; }
            if(result.MessageType!=WebSocketMessageType.Binary) throw new InvalidDataException("Relay tunnel carries binary messages only.");
            messageBytes+=result.Count;
            if(messageBytes>RelayProtocol.MessageBytes) throw new InvalidDataException("Relay message too large.");
            if(result.EndOfMessage) messageBytes=0;
            if(result.Count>0) return result.Count;
        }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        while(!buffer.IsEmpty)
        {
            int size=Math.Min(buffer.Length,RelayProtocol.MessageBytes);
            await socket.SendAsync(buffer[..size],WebSocketMessageType.Binary,true,token).ConfigureAwait(false); buffer=buffer[size..];
        }
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset,count),token).AsTask();
    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            // A full close handshake lets the peer read the last messages. Disposing straight after sending the close frame
            // can reset the TCP connection, and the relay's edge then drops data it has not yet forwarded.
            try
            {
                using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if(socket.State is WebSocketState.Open or WebSocketState.CloseReceived) socket.CloseAsync(WebSocketCloseStatus.NormalClosure,null,limit.Token).Wait(TimeSpan.FromSeconds(3));
            }
            catch { }
            socket.Dispose();
        }
        base.Dispose(disposing);
    }
}
