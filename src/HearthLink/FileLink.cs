using HearthLink.Core;
using System.Security.Cryptography;

namespace HearthLink;
/// <summary>
/// Moves one file over a files connection in <see cref="FilePolicy.ChunkBytes"/> chunks. The sender pauses after every
/// <see cref="Window"/> bytes until the receiver acknowledges, so neither the relay nor a slow peer buffers a whole file.
/// The receiver acknowledges every window and once at the end; the sender drains those before "file-end".
/// </summary>
public static class FileLink
{
    public const int Window=4*1024*1024;
    public static async Task Send(Connection c,Stream source,long length,IProgress<long>? progress,CancellationToken token)
    {
        var id=Guid.NewGuid(); using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer=new byte[FilePolicy.ChunkBytes]; long offset=0, acked=0;
        while(offset<length)
        {
            int read=await source.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,length-offset)),token);
            if(read==0) throw new IOException("The file changed while it was being sent.");
            await c.Send(new("file-chunk",Chunk:new(id,offset,buffer[..read])),token);
            hash.AppendData(buffer,0,read); offset+=read; progress?.Report(offset);
            if(offset-acked>=Window) acked=await Ack(c,token);
        }
        while(acked<length) acked=await Ack(c,token);
        await c.Send(new("file-end",Code:Convert.ToHexString(hash.GetHashAndReset())),token);
    }
    static async Task<long> Ack(Connection c,CancellationToken token)
    {
        var reply=await c.Read(token);
        if(reply.Kind!="file-ack") throw new InvalidDataException(reply.Kind=="denied"?reply.Text??"The other PC stopped the transfer.":"Unexpected reply during a file transfer.");
        return reply.Sequence;
    }
    /// <summary>Receives into <paramref name="destination"/> through a temp file; nothing appears there unless length and SHA-256 match.</summary>
    public static async Task<string> Receive(Connection c,string destination,long length,IProgress<long>? progress,CancellationToken token)
    {
        await using var receiver=new FileReceiver(destination,length,Guid.NewGuid(),false);
        Guid? id=null; long acked=0;
        while(receiver.Received<length)
        {
            var packet=await c.Read(token);
            if(packet.Chunk is not {} chunk) throw new InvalidDataException("Unexpected message during a file transfer.");
            if((id??=chunk.Id)!=chunk.Id) throw new InvalidDataException("File chunks from two transfers were mixed.");
            await receiver.AppendAsync(chunk.Offset,chunk.Bytes,token); progress?.Report(receiver.Received);
            if(receiver.Received-acked>=Window || receiver.Received==length) { acked=receiver.Received; await c.Send(new("file-ack",Sequence:acked),token); }
        }
        var end=await c.Read(token);
        if(end.Kind!="file-end" || end.Code==null) throw new InvalidDataException("File transfer ended without a digest.");
        await receiver.CommitAsync(end.Code,token);
        return destination;
    }
}
/// <summary>Host side of a files connection: answers list, get and put until the connection or the session ends. Writes only into the receive folder.</summary>
public sealed class FileHost(string receiveFolder)
{
    public async Task Serve(Connection c,CancellationToken lifetime)
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                var request=await c.Read(lifetime);
                switch(request.Kind)
                {
                    case "list": await c.Send(List(request.Text??""),lifetime); break;
                    case "get": await Get(c,request.Text??"",lifetime); break;
                    case "put": await Put(c,request,lifetime); break;
                    default: return;
                }
            }
        }
        catch(Exception e) when(e is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        finally { c.Dispose(); }
    }
    static Packet Denied(string text)=>new("denied",Text:text);
    static Packet List(string folder)
    {
        string? drives=null;
        if(folder.Length==0)
        {
            folder=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            drives=string.Join("|",DriveInfo.GetDrives().Where(d=>d.IsReady).Select(d=>d.RootDirectory.FullName));
        }
        if(!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return Denied("That folder no longer exists.");
        try
        {
            var options=new EnumerationOptions {IgnoreInaccessible=true,AttributesToSkip=FileAttributes.Hidden|FileAttributes.System};
            var entries=new DirectoryInfo(folder).EnumerateFileSystemInfos("*",options).Where(i=>FilePolicy.ValidName(i.Name))
                .Select(i=>new FileEntry(i.Name,i is DirectoryInfo,i is FileInfo f?f.Length:0,i.LastWriteTimeUtc))
                .OrderByDescending(e=>e.Folder).ThenBy(e=>e.Name,StringComparer.OrdinalIgnoreCase).Take(FileNames.MaxEntries);
            return new("listing",Text:Path.GetFullPath(folder),Data:FileNames.Encode(entries),Code:drives);
        }
        catch(Exception e) when(e is UnauthorizedAccessException or IOException) { return Denied("This PC's Windows account cannot open that folder."); }
    }
    static async Task Get(Connection c,string path,CancellationToken token)
    {
        if(!Path.IsPathFullyQualified(path) || !File.Exists(path)) { await c.Send(Denied("That file no longer exists."),token); return; }
        var info=new FileInfo(path);
        if(info.Length>FilePolicy.MaxFileBytes) { await c.Send(Denied("Files over 2 GiB cannot be copied."),token); return; }
        if(!FilePolicy.ValidName(info.Name)) { await c.Send(Denied("That file name cannot be copied."),token); return; }
        FileStream stream;
        try { stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,FilePolicy.ChunkBytes,FileOptions.Asynchronous|FileOptions.SequentialScan); }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) { await c.Send(Denied("Cannot read that file: it is in use or access is denied."),token); return; }
        await using(stream)
        {
            await c.Send(new("file",Name:info.Name,Sequence:stream.Length),token);
            await FileLink.Send(c,stream,stream.Length,null,token);
        }
    }
    async Task Put(Connection c,Packet request,CancellationToken token)
    {
        string name=request.Name??""; long length=request.Sequence;
        if(!FilePolicy.ValidName(name)) { await c.Send(Denied("That file name is not allowed."),token); return; }
        if(length<0 || length>FilePolicy.MaxFileBytes) { await c.Send(Denied("Files over 2 GiB cannot be copied."),token); return; }
        Directory.CreateDirectory(receiveFolder);
        string destination=FileNames.Unique(receiveFolder,name);
        await c.Send(new("go"),token);
        string saved=await FileLink.Receive(c,destination,length,null,token);
        await c.Send(new("saved",Name:Path.GetFileName(saved)),token);
    }
}
/// <summary>Controller side of a files connection. One operation at a time; after a cancelled or failed transfer, open a new connection.</summary>
public sealed class FileClient(Connection c) : IDisposable
{
    public async Task<(string Folder,IReadOnlyList<FileEntry> Entries,string[] Drives)> List(string folder,CancellationToken token)
    {
        await c.Send(new("list",Text:folder),token);
        var reply=Expect(await c.Read(token),"listing");
        string[] drives=(reply.Code??"").Split('|',StringSplitOptions.RemoveEmptyEntries).Where(d=>d.Length==3 && char.IsAsciiLetter(d[0]) && d[1..]==":\\").ToArray();
        return (reply.Text??"",FileNames.Decode(reply.Data??"[]"),drives);
    }
    /// <summary>Copies a host file into <paramref name="localFolder"/> under a name that overwrites nothing; returns the saved path.</summary>
    public async Task<string> Get(string remotePath,string localFolder,IProgress<long>? progress,CancellationToken token)
    {
        await c.Send(new("get",Text:remotePath),token);
        var reply=Expect(await c.Read(token),"file");
        if(!FilePolicy.ValidName(reply.Name??"") || reply.Sequence<0 || reply.Sequence>FilePolicy.MaxFileBytes) throw new InvalidDataException("The other PC sent an invalid file name or size.");
        Directory.CreateDirectory(localFolder);
        return await FileLink.Receive(c,FileNames.Unique(localFolder,reply.Name!),reply.Sequence,progress,token);
    }
    /// <summary>Sends a local file to the host's receive folder; returns the name it was saved under.</summary>
    public async Task<string> Put(string localPath,IProgress<long>? progress,CancellationToken token)
    {
        await using var stream=new FileStream(localPath,FileMode.Open,FileAccess.Read,FileShare.Read,FilePolicy.ChunkBytes,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(stream.Length>FilePolicy.MaxFileBytes) throw new InvalidOperationException("Files over 2 GiB cannot be copied.");
        await c.Send(new("put",Name:Path.GetFileName(localPath),Sequence:stream.Length),token);
        Expect(await c.Read(token),"go");
        await FileLink.Send(c,stream,stream.Length,progress,token);
        return Expect(await c.Read(token),"saved").Name??"";
    }
    static Packet Expect(Packet reply,string kind)=>reply.Kind==kind ? reply : reply.Kind=="denied" ? throw new InvalidOperationException(reply.Text??"The other PC refused.") : throw new InvalidDataException("Unexpected reply: "+reply.Kind);
    public void Dispose()=>c.Dispose();
}
