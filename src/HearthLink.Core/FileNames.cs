using System.Text.Json;
namespace HearthLink.Core;
/// <summary>One item of a remote folder listing. Names come from the other PC and are checked with <see cref="FilePolicy.ValidName"/>.</summary>
public sealed record FileEntry(string Name,bool Folder,long Size,DateTimeOffset Modified);
public static class FileNames
{
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
    public static string Encode(IEnumerable<FileEntry> entries)=>JsonSerializer.Serialize(entries.Take(MaxEntries));
    /// <summary>Reads a listing from the other PC, dropping entries with unsafe names or negative sizes.</summary>
    public static IReadOnlyList<FileEntry> Decode(string json)
    {
        FileEntry[]? entries;
        try { entries=JsonSerializer.Deserialize<FileEntry[]>(json); } catch(JsonException e) { throw new InvalidDataException("Invalid file listing.",e); }
        return (entries??[]).Where(e=>e!=null && FilePolicy.ValidName(e.Name) && e.Size>=0).Take(MaxEntries).ToArray();
    }
}
