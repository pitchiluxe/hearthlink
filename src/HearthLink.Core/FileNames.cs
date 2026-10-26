using System.Text.Json;
namespace HearthLink.Core;
/// <summary>One item of a remote folder listing. Names come from the other PC and are checked with <see cref="FilePolicy.ValidName"/>.</summary>
public sealed record FileEntry(string Name,bool Folder,long Size,DateTimeOffset Modified);
/// <summary>A ready drive on a PC, as shown in the file manager's drive bar ("C:\", label, free and total bytes).</summary>
public sealed record DriveEntry(string Root,string Label,long Free,long Total);
public static class FileNames
{
    public static string EncodeDrives(IEnumerable<DriveEntry> drives)=>JsonSerializer.Serialize(drives.Take(26));
    /// <summary>Reads the other PC's drive list, keeping only plain drive roots ("C:\") with sane sizes.</summary>
    public static IReadOnlyList<DriveEntry> DecodeDrives(string json)
    {
        DriveEntry[]? drives;
        try { drives=JsonSerializer.Deserialize<DriveEntry[]>(json); } catch(JsonException) { return []; }
        return (drives??[]).Where(d=>d?.Root is {Length:3} r && char.IsAsciiLetter(r[0]) && r[1..]==":\\" && d.Free>=0 && d.Total>=0)
            .Select(d=>d with {Label=new string((d.Label??"").Where(c=>!char.IsControl(c)).Take(32).ToArray())}).Take(26).ToArray();
    }
    public const int MaxEntries=5000;
    /// <summary>A path in <paramref name="folder"/> that does not exist yet: the name, then "stem (2).ext", "stem (3).ext"… so nothing is overwritten.</summary>
    public static string Unique(string folder,string name)
    {
        if(!FilePolicy.ValidName(name)) throw new InvalidDataException("Unsafe file name.");
        string stem=Path.GetFileNameWithoutExtension(name), extension=Path.GetExtension(name);
        for(int n=1;n<=9999;n++)
        {
            string candidate=Path.Combine(folder,n==1?name:$"{stem} ({n}){extension}");
            if(!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Too many files named "+name+".");
    }
    public const int MaxDepth=32, MaxRelativeLength=400;
    /// <summary>A relative path from the other PC ("Album\2026\photo.jpg") as its segments; each must pass <see cref="FilePolicy.ValidName"/>, so ".." and drive or root paths are refused.</summary>
    public static string[] SplitRelative(string path)
    {
        if(string.IsNullOrEmpty(path) || path.Length>MaxRelativeLength) throw new InvalidDataException("Unsafe relative path.");
        string[] segments=path.Split('\\');
        if(segments.Length>MaxDepth || !segments.All(FilePolicy.ValidName)) throw new InvalidDataException("Unsafe relative path.");
        return segments;
    }
    public static string Encode(IEnumerable<FileEntry> entries)=>JsonSerializer.Serialize(entries.Take(MaxEntries));
    /// <summary>Reads a listing from the other PC, dropping entries with unsafe names or negative sizes.</summary>
    public static IReadOnlyList<FileEntry> Decode(string json)
    {
        FileEntry[]? entries;
        try { entries=JsonSerializer.Deserialize<FileEntry[]>(json); } catch(JsonException e) { throw new InvalidDataException("Invalid file listing.",e); }
        return (entries??[]).Where(e=>e!=null && FilePolicy.ValidName(e.Name) && e.Size>=0).Take(MaxEntries).ToArray();
    }
}
