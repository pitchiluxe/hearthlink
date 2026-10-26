namespace HearthLink.Core;
public static class FilePolicy
{
    public const int ChunkBytes=64*1024;
    public static bool ValidName(string name)
    {
        if(string.IsNullOrWhiteSpace(name) || name.Length>200 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')) return false;
        if(name.Any(c=>char.IsControl(c) || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.Format || "\\/:*?\"<>|".Contains(c))) return false;
        string stem=name.Split('.')[0].ToUpperInvariant();
        if(stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") return false;
        return !(stem.Length==4 && (stem.StartsWith("COM",StringComparison.Ordinal) || stem.StartsWith("LPT",StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]));
    }
}
