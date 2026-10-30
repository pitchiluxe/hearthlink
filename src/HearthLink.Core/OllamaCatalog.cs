using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace HearthLink.Core;
public sealed record CatalogModel(string Name,string Description,string Variants,DateTimeOffset? Updated)
{
    public Uri Page=>new("https://ollama.com/library/"+Uri.EscapeDataString(Name));
}
public sealed record CatalogScan(IReadOnlyList<CatalogModel> Models,bool Partial,DateTimeOffset Scanned);
/// <summary>Public metadata only. No installation, local paths, keys or prompts are sent to the catalog.</summary>
public sealed class OllamaCatalog(HttpClient client)
{
    static readonly TimeSpan RegexLimit=TimeSpan.FromSeconds(1);
    public static IReadOnlyList<CatalogModel> Parse(string html)
    {
        if(html.Length>5*1024*1024) throw new InvalidDataException("Catalog page is too large.");
        var result=new List<CatalogModel>();
        foreach(Match match in Regex.Matches(html,"<a\\s+[^>]*href=\"/library/(?<name>[a-zA-Z0-9._-]{1,100})\"[^>]*>(?<body>[\\s\\S]*?)</a>",RegexOptions.CultureInvariant,RegexLimit))
        {
            string body=match.Groups["body"].Value;
            string[] badges=Regex.Matches(body,"<span[^>]*>\\s*(?<badge>tools|thinking|vision|embedding|cloud|[0-9]+(?:\\.[0-9]+)?[bBmM])\\s*</span>",RegexOptions.CultureInvariant,RegexLimit)
                .Select(m=>m.Groups["badge"].Value.ToLowerInvariant()).Distinct().ToArray();
            if(badges.Contains("embedding")) continue;
            var sizes=badges.Where(b=>Regex.IsMatch(b,"^[0-9]+(?:\\.[0-9]+)?[bm]$",RegexOptions.CultureInvariant,RegexLimit)).ToArray();
            if(badges.Contains("cloud") && sizes.Length==0) continue;
            string summary=Regex.Match(body,"<p[^>]*>(?<text>[\\s\\S]*?)</p>",RegexOptions.CultureInvariant,RegexLimit).Groups["text"].Value;
            summary=WebUtility.HtmlDecode(Regex.Replace(summary,"<[^>]+>"," ",RegexOptions.CultureInvariant,RegexLimit));
            summary=Regex.Replace(summary,"\\s+"," ",RegexOptions.CultureInvariant,RegexLimit).Trim();
            string timestamp=Regex.Match(body,"title=\"(?<date>[^\"]+ UTC)\"",RegexOptions.CultureInvariant,RegexLimit).Groups["date"].Value.Replace(" UTC"," +00:00",StringComparison.Ordinal);
            DateTimeOffset? date=DateTimeOffset.TryParse(timestamp,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out var parsed)?parsed:null;
            result.Add(new(match.Groups["name"].Value,summary[..Math.Min(summary.Length,240)],string.Join(", ",sizes),date));
            if(result.Count>=1000) break;
        }
        return result.DistinctBy(m=>m.Name).ToArray();
    }
    public async Task<CatalogScan> Scan(CancellationToken token)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var models=new Dictionary<string,CatalogModel>(StringComparer.Ordinal);
        bool partial=false;
        for(int page=1;page<=10;page++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://ollama.com/library?sort=newest"+(page==1?"":"&page="+page));
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            string html=await AiHttp.Read(response,"Ollama Library",5*1024*1024,timeout.Token);
            var found=Parse(html);
            if(found.Count==0 && page==1) throw new InvalidDataException("Ollama Library could not be parsed. Installed models remain usable; open the library in your browser.");
            int added=0; foreach(var model in found) if(models.TryAdd(model.Name,model)) added++;
            bool more=Regex.IsMatch(html,"(?:href|hx-get)=\"[^\"]*(?:[?&]|&amp;)page="+(page+1)+"(?:[&\"]|&amp;)",RegexOptions.CultureInvariant,RegexLimit);
            if(!more || added==0) break;
            if(page==10 || models.Count>=1000) {partial=true; break;}
        }
        return new(models.Values.Take(1000).ToArray(),partial,DateTimeOffset.Now);
    }
}
