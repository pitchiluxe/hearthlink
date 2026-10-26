using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace HearthLink.Core;
public sealed record Packet(string Kind, string? Text=null, string? Name=null, string? Code=null,
    string? Data=null, int X=0, int Y=0, int Value=0, bool Down=false, bool Control=false, string[]? Monitors=null,
    long Sequence=0,[property:JsonIgnore] VideoFrame? Frame=null,[property:JsonIgnore] FileChunk? Chunk=null);
public static class Wire
{
    public const int MaxLength=16*1024*1024;
    public const int ProtocolVersion=3;
    const int VideoHeader=36;
    const int VideoMagic=0x484C4632;
    const int ChunkMagic=0x484C4332;
    public static async Task Write(Stream stream,Packet packet,CancellationToken token=default)
    {
        if(packet.Chunk is {} chunk)
        {
            if(packet.Frame!=null || chunk.Id==Guid.Empty || chunk.Offset<0 || chunk.Bytes.Length<1 || chunk.Bytes.Length>FilePolicy.ChunkBytes) throw new InvalidDataException("Invalid file chunk.");
            byte[] chunkHeader=new byte[36]; BinaryPrimitives.WriteInt32BigEndian(chunkHeader,-(32+chunk.Bytes.Length)); BinaryPrimitives.WriteInt32BigEndian(chunkHeader.AsSpan(4),ChunkMagic);
            chunk.Id.TryWriteBytes(chunkHeader.AsSpan(8,16)); BinaryPrimitives.WriteInt64BigEndian(chunkHeader.AsSpan(24),chunk.Offset); BinaryPrimitives.WriteInt32BigEndian(chunkHeader.AsSpan(32),chunk.Bytes.Length);
            await stream.WriteAsync(chunkHeader,token).ConfigureAwait(false); await stream.WriteAsync(chunk.Bytes,token).ConfigureAwait(false); return;
        }
        if(packet.Frame is {} frame)
        {
            Validate(frame.Width,frame.Height,frame.Monitor,frame.Sequence,frame.CaptureMicroseconds,frame.EncodeMicroseconds,frame.RoundTripMilliseconds);
            if(packet.Kind!="frame" || frame.Jpeg.Length==0 || frame.Jpeg.Length>MaxLength-VideoHeader) throw new InvalidDataException("Invalid video payload.");
            byte[] videoHeader=new byte[4+VideoHeader];
            BinaryPrimitives.WriteInt32BigEndian(videoHeader,-(VideoHeader+frame.Jpeg.Length));
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(4),VideoMagic);
            BinaryPrimitives.WriteInt64BigEndian(videoHeader.AsSpan(8),frame.Sequence);
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(16),frame.Width);
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(20),frame.Height);
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(24),frame.Monitor);
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(28),frame.CaptureMicroseconds);
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(32),frame.EncodeMicroseconds);
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(36),frame.RoundTripMilliseconds);
            await stream.WriteAsync(videoHeader,token).ConfigureAwait(false);
            await stream.WriteAsync(frame.Jpeg,token).ConfigureAwait(false); return;
        }
        byte[] data=JsonSerializer.SerializeToUtf8Bytes(packet);
        if(data.Length>MaxLength) throw new InvalidDataException("Frame too large.");
        byte[] header=new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header,data.Length);
        await stream.WriteAsync(header,token).ConfigureAwait(false); await stream.WriteAsync(data,token).ConfigureAwait(false);
    }
    public static async Task<Packet> Read(Stream stream,CancellationToken token=default)
    {
        byte[] header=new byte[4]; await stream.ReadExactlyAsync(header,token).ConfigureAwait(false);
        int size=BinaryPrimitives.ReadInt32BigEndian(header);
        if(size<0)
        {
            if(size==int.MinValue || -size<5 || -size>MaxLength) throw new InvalidDataException("Invalid binary length.");
            byte[] magic=new byte[4]; await stream.ReadExactlyAsync(magic,token).ConfigureAwait(false);
            int kind=BinaryPrimitives.ReadInt32BigEndian(magic);
            if(kind==ChunkMagic)
            {
                if(-size<=32 || -size>32+FilePolicy.ChunkBytes) throw new InvalidDataException("Invalid file chunk size.");
                byte[] meta=new byte[28]; await stream.ReadExactlyAsync(meta,token).ConfigureAwait(false);
                Guid id=new(meta.AsSpan(0,16)); long offset=BinaryPrimitives.ReadInt64BigEndian(meta.AsSpan(16)); int length=BinaryPrimitives.ReadInt32BigEndian(meta.AsSpan(24));
                if(id==Guid.Empty || offset<0 || length!=(-size-32)) throw new InvalidDataException("Invalid file chunk metadata.");
                byte[] bytes=new byte[length]; await stream.ReadExactlyAsync(bytes,token).ConfigureAwait(false); return new("file-chunk",Chunk:new(id,offset,bytes));
            }
            if(kind!=VideoMagic || -size<=VideoHeader) throw new InvalidDataException("Unknown video protocol.");
            byte[] metadata=new byte[VideoHeader]; magic.CopyTo(metadata,0); await stream.ReadExactlyAsync(metadata.AsMemory(4),token).ConfigureAwait(false);
            long sequence=BinaryPrimitives.ReadInt64BigEndian(metadata.AsSpan(4));
            int width=BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(12)),height=BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(16));
            int monitor=BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(20)),capture=BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(24));
            int encode=BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(28)),roundTrip=BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(32));
            Validate(width,height,monitor,sequence,capture,encode,roundTrip);
            byte[] jpeg=new byte[-size-VideoHeader]; await stream.ReadExactlyAsync(jpeg,token).ConfigureAwait(false);
            return new("frame",Frame:new(jpeg,width,height,monitor,sequence,capture,encode,roundTrip));
        }
        if(size<=0 || size>MaxLength) throw new InvalidDataException("Invalid frame length.");
        byte[] data=new byte[size]; await stream.ReadExactlyAsync(data,token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Packet>(data) ?? throw new InvalidDataException("Invalid message.");
    }
    static void Validate(int width,int height,int monitor,long sequence,int capture,int encode,int roundTrip)
    {
        if(width<1 || height<1 || width>8192 || height>8192 || (long)width*height>34000000 || monitor<0 || monitor>63 || sequence<=0 || capture<0 || encode<0 || roundTrip<0 || roundTrip>60000)
            throw new InvalidDataException("Invalid video metadata.");
    }
}
