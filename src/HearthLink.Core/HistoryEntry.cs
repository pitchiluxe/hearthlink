using System.Text.Json;

namespace HearthLink.Core;

public sealed record HistoryEntry(DateTimeOffset At,string Peer,string Outcome)
{
    public static HistoryEntry? Parse(string line)
    {
        if(line.Length>2048) return null;
        try
        {
            using var json=JsonDocument.Parse(line);
            var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
                !root.TryGetProperty("at",out var at) || at.ValueKind!=JsonValueKind.String || !at.TryGetDateTimeOffset(out var time) ||
                !root.TryGetProperty("peer",out var peer) || peer.ValueKind!=JsonValueKind.String ||
                !root.TryGetProperty("outcome",out var outcome) || outcome.ValueKind!=JsonValueKind.String) return null;
            string fingerprint=peer.GetString()!, result=outcome.GetString()!;
            if(fingerprint.Length!=64 || !fingerprint.All(Uri.IsHexDigit) || result.Length is <1 or >160 ||
                result.Any(c=>char.IsControl(c) || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.Format)) return null;
            return new(time,fingerprint.ToUpperInvariant(),result);
        }
        catch(JsonException) { return null; }
    }
}
