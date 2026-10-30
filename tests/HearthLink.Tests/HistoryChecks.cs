using HearthLink.Core;
using System.Text.Json;

static class HistoryChecks
{
    public static void Run(Action<bool,string> check)
    {
        string peer=new('A',64);
        string record=JsonSerializer.Serialize(new {at=DateTimeOffset.UtcNow,peer,outcome="outbound-connected"});
        check(HistoryEntry.Parse(record) is {Peer:var p,Outcome:"outbound-connected"} && p==peer,"history parses valid audit metadata");
        check(new[]{"", "{", "null", "[]", "{\"at\":5}",new string('x',2049)}.All(line=>HistoryEntry.Parse(line)==null),"history ignores malformed, truncated and oversized lines");
        string Record(string fingerprint,string outcome)=>JsonSerializer.Serialize(new {at=DateTimeOffset.UtcNow,peer=fingerprint,outcome});
        check(HistoryEntry.Parse(Record("bad","connected"))==null && HistoryEntry.Parse(Record(peer,"line\nbreak"))==null && HistoryEntry.Parse(Record(peer,"\u202Etext"))==null,"history rejects invalid fingerprints and control/direction text");
        check(HistoryEntry.Parse(Record(peer.ToLowerInvariant(),"connected"))?.Peer==peer,"history normalizes fingerprints for paired-identity matching");
    }
}
