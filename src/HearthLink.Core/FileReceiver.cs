using System.Security.Cryptography;
namespace HearthLink.Core;
public sealed class FileReceiver : IAsyncDisposable
{
    readonly string destination,temp;
    readonly long expected;
    readonly bool overwrite;
    readonly FileStream stream;
    readonly IncrementalHash hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    bool committed,closed;
    public long Received {get;private set;}
    public FileReceiver(string destination,long length,Guid id,bool overwrite)
    {
        if(length<0 || id==Guid.Empty || !Path.IsPathFullyQualified(destination)) throw new InvalidDataException("Invalid file destination or size.");
        this.destination=Path.GetFullPath(destination); expected=length; this.overwrite=overwrite;
        temp=Path.Combine(Path.GetDirectoryName(this.destination)!,"hearthlink-"+id.ToString("N")+".partial");
        stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,FilePolicy.ChunkBytes,FileOptions.Asynchronous|FileOptions.SequentialScan);
    }
    public async Task AppendAsync(long offset,ReadOnlyMemory<byte> data,CancellationToken token=default)
    {
        if(closed || offset!=Received || data.Length==0 || data.Length>FilePolicy.ChunkBytes || data.Length>expected-Received) throw new InvalidDataException("Unexpected file chunk or offset.");
        await stream.WriteAsync(data,token).ConfigureAwait(false); hash.AppendData(data.Span); Received+=data.Length;
    }
    public async Task CommitAsync(string expectedHash,CancellationToken token=default)
    {
        if(closed || Received!=expected || expectedHash.Length!=64) throw new InvalidDataException("File is incomplete.");
        byte[] remoteHash;
        try {remoteHash=Convert.FromHexString(expectedHash);} catch(FormatException e) {throw new InvalidDataException("Invalid file digest.",e);}
        if(!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),remoteHash)) throw new InvalidDataException("File integrity check failed.");
        await stream.FlushAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); await stream.DisposeAsync(); closed=true;
        File.Move(temp,destination,overwrite); committed=true;
    }
    public async ValueTask DisposeAsync()
    {
        if(!closed) {await stream.DisposeAsync(); closed=true;}
        hash.Dispose();
        if(!committed) {try {File.Delete(temp);} catch(IOException) { } catch(UnauthorizedAccessException) { }}
    }
}
