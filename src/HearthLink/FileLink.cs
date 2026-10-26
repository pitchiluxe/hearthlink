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
    /// <summary>Free bytes on the drive holding <paramref name="folder"/>; unknown (network paths) counts as unlimited, and the write itself still fails cleanly.</summary>
    public static long FreeSpace(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch(Exception e) when(e is ArgumentException or IOException or UnauthorizedAccessException) { return long.MaxValue; }
    }
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
public sealed class FileHost(string receiveFolder,Func<string,long>? freeSpace=null)
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
                    case "list": await c.Send(List(request.Text??"",request.Value),lifetime); break;
                    case "get": await Get(c,request.Text??"",lifetime); break;
                    case "put": await Put(c,request,lifetime); break;
                    case "folder": await Folder(c,request,lifetime); break;
                    default: return;
                }
            }
        }
        catch(Exception e) when(e is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        finally { c.Dispose(); }
    }
    static Packet Denied(string text)=>new("denied",Text:text);
    /// <summary>This PC's ready drives with label and space, for the file manager's drive bar.</summary>
    public static IEnumerable<DriveEntry> Drives()
    {
        foreach(var d in DriveInfo.GetDrives())
        {
            DriveEntry? entry=null;
            try { if(d.IsReady) entry=new(d.RootDirectory.FullName,d.VolumeLabel,d.AvailableFreeSpace,d.TotalSize); } catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
            if(entry!=null) yield return entry;
        }
    }
    /// <summary>One page of a folder listing: up to <see cref="FileNames.MaxEntries"/> entries from <paramref name="offset"/>, with the total in Sequence so the client asks for the rest.</summary>
    static Packet List(string folder,int offset)
    {
        string? drives=null;
        if(folder.Length==0)
        {
            folder=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            drives=FileNames.EncodeDrives(Drives());
        }
        if(!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return Denied("That folder no longer exists.");
        try { var (full,entries)=Read(folder); return new("listing",Text:full,Data:FileNames.Encode(entries.Skip(Math.Max(0,offset))),Code:drives,Sequence:entries.Length); }
        catch(Exception e) when(e is UnauthorizedAccessException or IOException) { return Denied("This PC's Windows account cannot open that folder."); }
    }
    /// <summary>A folder's entries as both file manager panes show them: folders first, then by name; hidden and system items left out.</summary>
    public static (string Folder,FileEntry[] Entries) Read(string folder)
    {
        var options=new EnumerationOptions {IgnoreInaccessible=true,AttributesToSkip=FileAttributes.Hidden|FileAttributes.System};
        var entries=new DirectoryInfo(folder).EnumerateFileSystemInfos("*",options).Where(i=>FilePolicy.ValidName(i.Name))
            .Select(i=>new FileEntry(i.Name,i is DirectoryInfo,i is FileInfo f?f.Length:0,i.LastWriteTimeUtc))
            .OrderByDescending(e=>e.Folder).ThenBy(e=>e.Name,StringComparer.OrdinalIgnoreCase).ToArray();
        return (Path.GetFullPath(folder),entries);
    }
    static async Task Get(Connection c,string path,CancellationToken token)
    {
        if(!Path.IsPathFullyQualified(path) || !File.Exists(path)) { await c.Send(Denied("That file no longer exists."),token); return; }
        var info=new FileInfo(path);
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
    /// <summary>
    /// Where a put or folder lands: the folder the controller picked in its file manager (Text: an existing absolute folder,
    /// written as this PC's signed-in user) or, if none, the receive folder. Null when the pick is not an existing absolute folder.
    /// </summary>
    string? Base(Packet request)
    {
        if(string.IsNullOrEmpty(request.Text)) { Directory.CreateDirectory(receiveFolder); return Path.GetFullPath(receiveFolder); }
        return Path.IsPathFullyQualified(request.Text) && Directory.Exists(request.Text) ? Path.GetFullPath(request.Text) : null;
    }
    /// <summary><paramref name="root"/> joined with a relative path from the other PC, or null if any segment is unsafe or the result would leave <paramref name="root"/>.</summary>
    static string? Inside(string root,string relative)
    {
        string[] segments; try { segments=FileNames.SplitRelative(relative); } catch(InvalidDataException) { return null; }
        string full=Path.GetFullPath(Path.Combine([root,..segments]));
        return full.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase) ? full : null;
    }
    /// <summary>Creates a folder for a sent folder tree. A single-segment (top) folder gets a unique name so a second copy never merges into the first.</summary>
    async Task Folder(Connection c,Packet request,CancellationToken token)
    {
        string relative=request.Name??"";
        if(Base(request) is not {} root) { await c.Send(Denied("That folder no longer exists on this PC."),token); return; }
        if(Inside(root,relative) is not {} full) { await c.Send(Denied("That folder name is not allowed."),token); return; }
        if(!relative.Contains('\\')) full=FileNames.Unique(root,relative);
        Directory.CreateDirectory(full);
        await c.Send(new("folder-ok",Name:Path.GetRelativePath(root,full)),token);
    }
    async Task Put(Connection c,Packet request,CancellationToken token)
    {
        string name=request.Name??""; long length=request.Sequence;
        if(Base(request) is not {} root) { await c.Send(Denied("That folder no longer exists on this PC."),token); return; }
        // A name may be a path inside a folder the controller created with "folder" ("Album\2026\photo.jpg").
        if(Inside(root,name) is not {} target) { await c.Send(Denied("That file name is not allowed."),token); return; }
        if(length<0) { await c.Send(Denied("Invalid file size."),token); return; }
        long free=(freeSpace??FileLink.FreeSpace)(root);
        if(length>free) { await c.Send(Denied($"Not enough free space on the other PC: needs {FilePane.SizeText(length)}, has {FilePane.SizeText(free)}."),token); return; }
        string folder=Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        string destination=FileNames.Unique(folder,Path.GetFileName(target));
        await c.Send(new("go"),token);
        string saved=await FileLink.Receive(c,destination,length,null,token);
        await c.Send(new("saved",Name:Path.GetRelativePath(root,saved)),token);
    }
}
/// <summary>Controller side of a files connection. One operation at a time; after a cancelled or failed transfer, open a new connection.</summary>
public sealed class FileClient(Connection c,Func<string,long>? freeSpace=null) : IDisposable
{
    /// <summary>Largest folder listing accepted from the other PC.</summary>
    public const int MaxListing=1_000_000;
    public async Task<(string Folder,IReadOnlyList<FileEntry> Entries,IReadOnlyList<DriveEntry> Drives)> List(string folder,CancellationToken token)
    {
        await c.Send(new("list",Text:folder),token);
        var reply=Expect(await c.Read(token),"listing");
        var entries=new List<FileEntry>(FileNames.Decode(reply.Data??"[]"));
        // Big folders come in pages; ask for the rest of the same folder until the total the host reported has arrived.
        long total=Math.Min(reply.Sequence,MaxListing);
        while(entries.Count<total)
        {
            await c.Send(new("list",Text:reply.Text,Value:entries.Count),token);
            var page=FileNames.Decode(Expect(await c.Read(token),"listing").Data??"[]");
            if(page.Count==0) break;
            entries.AddRange(page);
        }
        return (reply.Text??"",entries,reply.Code==null?[]:FileNames.DecodeDrives(reply.Code));
    }
    /// <summary>Copies a host file into <paramref name="localFolder"/> under a name that overwrites nothing; returns the saved path.</summary>
    public async Task<string> Get(string remotePath,string localFolder,IProgress<long>? progress,CancellationToken token)
    {
        await c.Send(new("get",Text:remotePath),token);
        var reply=Expect(await c.Read(token),"file");
        if(!FilePolicy.ValidName(reply.Name??"") || reply.Sequence<0) throw new InvalidDataException("The other PC sent an invalid file name or size.");
        Directory.CreateDirectory(localFolder);
        long free=(freeSpace??FileLink.FreeSpace)(localFolder);
        if(reply.Sequence>free)
        {
            // The host is already sending; this connection cannot be reused, so callers open a new one.
            c.Dispose();
            throw new InvalidOperationException($"Not enough free space on this PC: needs {FilePane.SizeText(reply.Sequence)}, has {FilePane.SizeText(free)}.");
        }
        return await FileLink.Receive(c,FileNames.Unique(localFolder,reply.Name!),reply.Sequence,progress,token);
    }
    /// <summary>Creates a folder in the host's receive folder; a top-level name comes back unique ("Album (2)"). Returns the folder's path relative to the receive folder.</summary>
    public async Task<string> Folder(string relative,CancellationToken token,string? remoteFolder=null)
    {
        await c.Send(new("folder",Name:relative,Text:remoteFolder),token);
        return Expect(await c.Read(token),"folder-ok").Name??"";
    }
    /// <summary>Sends a local file to the host's receive folder, optionally to a path inside a folder made with <see cref="Folder"/>; returns the relative name it was saved under.</summary>
    public async Task<string> Put(string localPath,IProgress<long>? progress,CancellationToken token,string? remoteName=null,string? remoteFolder=null)
    {
        await using var stream=new FileStream(localPath,FileMode.Open,FileAccess.Read,FileShare.Read,FilePolicy.ChunkBytes,FileOptions.Asynchronous|FileOptions.SequentialScan);
        await c.Send(new("put",Name:remoteName??Path.GetFileName(localPath),Sequence:stream.Length,Text:remoteFolder),token);
        Expect(await c.Read(token),"go");
        await FileLink.Send(c,stream,stream.Length,progress,token);
        return Expect(await c.Read(token),"saved").Name??"";
    }
    static Packet Expect(Packet reply,string kind)=>reply.Kind==kind ? reply : reply.Kind=="denied" ? throw new InvalidOperationException(reply.Text??"The other PC refused.") : throw new InvalidDataException("Unexpected reply: "+reply.Kind);
    public void Dispose()=>c.Dispose();
}
