using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Scout.Core;

public interface IEpisodeDescriptions
{
    Task<List<Episode>> CompleteDescriptionsAsync(string seriesId,IReadOnlyList<Episode> episodes,CancellationToken ct);
}

public sealed partial class MetadataCatalog
{
    public async Task<List<Episode>> CompleteDescriptionsAsync(string seriesId,IReadOnlyList<Episode> episodes,CancellationToken ct)
    {
        if(!long.TryParse(seriesId,out var number)||number<=0)throw new ArgumentException("Invalid series ID.");
        var result=episodes.ToList();
        string validationKey=DescriptionRepair.ValidationKey(episodes);
        var suspects=provider is "tvdb" or "tmdb"?episodes.Where(e=>DescriptionRepair.Suspect(e,episodes)&&
            e.DescriptionValidationKey!=validationKey).Select(e=>e.Id).ToHashSet():[];
        if(result.All(e=>!string.IsNullOrWhiteSpace(e.Overview))&&suspects.Count==0)return result;
        // Fill only blank descriptions. The selected provider's IDs, titles and
        // numbering remain intact, including when a supplemental provider differs.
        for(int i=0;i<result.Count;i++) {
            var episode=result[i];if(!string.IsNullOrWhiteSpace(episode.Overview))continue;
            ct.ThrowIfCancellationRequested();
            try {
                var text=await EpisodeDescription(seriesId,episode,ct);
                if(!string.IsNullOrWhiteSpace(text))result[i]=Recovered(episode,text,provider,seriesId,episode.Id,"provider-episode-details");
            }catch(Exception ex) when(Recoverable(ex,ct)) {
                result[i]=episode with {DescriptionIssue=$"{provider.ToUpperInvariant()} episode details were unavailable or did not match the saved episode."};
            }
        }
        if((result.Any(e=>string.IsNullOrWhiteSpace(e.Overview))||suspects.Count>0)&&provider is "tvdb" or "tmdb") {
            try {
                var supplemental=await TvmazeSupplement(seriesId,ct);
                if(supplemental!=null)for(int i=0;i<result.Count;i++) {
                    var episode=result[i];
                    if(!string.IsNullOrWhiteSpace(episode.Overview)) {
                        if(!suspects.Contains(episode.Id))continue;
                        var replacement=DescriptionRepair.Replacement(episode,episodes,supplemental.Value.Episodes);
                        if(replacement!=null)result[i]=Recovered(episode,replacement.Overview,"tvmaze",supplemental.Value.Id,replacement.Id,"verified-series-unique-title-misplaced-description") with {
                            OriginalDescription=new(episode.Overview,episode.DescriptionSource),DescriptionValidationKey=validationKey};
                        else result[i]=episode with {DescriptionValidationKey=validationKey};
                        continue;
                    }
                    var match=DescriptionMapping.Find(episode,episodes,supplemental.Value.Episodes);
                    if(match!=null&&!string.IsNullOrWhiteSpace(match.Overview))
                        result[i]=Recovered(episode,match.Overview,"tvmaze",supplemental.Value.Id,match.Id,"external-series-id-and-aligned-episode");
                }
            }catch(Exception ex) when(Recoverable(ex,ct)) {
                for(int i=0;i<result.Count;i++)if(string.IsNullOrWhiteSpace(result[i].Overview))
                    result[i]=result[i] with {DescriptionIssue="Supplemental episode descriptions were unavailable or their series identity could not be verified."};
            }
        }
        var updatedValidationKey=DescriptionRepair.ValidationKey(result);
        result=result.Select(e=>e.DescriptionValidationKey==validationKey?e with {DescriptionValidationKey=updatedValidationKey}:e).ToList();
        return result.Select(e=>string.IsNullOrWhiteSpace(e.Overview)
            ?e with {DescriptionIssue=e.DescriptionIssue??"No description was found with a verified series and episode mapping. This episode cannot be accepted without guide evidence."}:e).ToList();
    }

    static bool Recoverable(Exception ex,CancellationToken ct)=>!ct.IsCancellationRequested&&ex is HttpRequestException or IOException or InvalidDataException or TimeoutException or JsonException or InvalidOperationException or ArgumentException or OperationCanceledException;
    static string Description(string text)=>WebUtility.HtmlDecode(Regex.Replace(text,"<[^>]*>"," ")).Trim();
    static Episode Recovered(Episode episode,string text,string source,string series,int episodeId,string method)=>episode with {
        Overview=Description(text),DescriptionIssue=null,
        DescriptionSource=new(source,series,episodeId.ToString(),source switch {
            "tvdb"=>$"https://thetvdb.com/dereferrer/episode/{episodeId}",
            "tvmaze"=>$"https://www.tvmaze.com/episodes/{episodeId}",
            _=>$"https://www.themoviedb.org/tv/{series}/season/{episode.Season}/episode/{episode.Number}"
        },method,DateTimeOffset.UtcNow)
    };

    async Task<string?> EpisodeDescription(string series,Episode episode,CancellationToken ct)
    {
        if(provider=="tvdb") {
            var row=(await Request($"episodes/{episode.Id}/extended?meta=translations",ct))?["data"];
            if(row==null)return null;
            if(row.Int("id")!=episode.Id||row["seriesId"]?.ToString()!=series)throw new InvalidDataException("Episode identity mismatch.");
            var english=row["translations"].Array("overviewTranslations").Where(t=>t.Text("language")=="eng"&&!string.IsNullOrWhiteSpace(t.Text("overview"))).ToList();
            if(english.Count==1)return Description(english[0].Text("overview"));
            var overview=Description(row.Text("overview"));if(overview.Length>0)return overview;
            var translated=(await Request($"episodes/{episode.Id}/translations/eng",ct))?["data"];
            if(translated==null)return null;
            if(translated.Text("language")!="eng")throw new InvalidDataException("Episode translation language mismatch.");
            return Description(translated.Text("overview"));
        }
        var node=await Request(provider=="tmdb"?$"tv/{series}/season/{episode.Season}/episode/{episode.Number}?language=en-US":$"episodes/{episode.Id}?embed=show",ct);
        if(node==null)return null;
        if(node.Int("id")!=episode.Id||node.Int(provider=="tmdb"?"season_number":"season",-1)!=episode.Season||node.Int(provider=="tmdb"?"episode_number":"number",-1)!=episode.Number)
            throw new InvalidDataException("Episode identity mismatch.");
        if(provider=="tvmaze"&&node["_embedded"]?["show"]?["id"]?.ToString()!=series)throw new InvalidDataException("Episode series mismatch.");
        return Description(node.Text(provider=="tvmaze"?"summary":"overview"));
    }

    async Task<(string Id,List<Episode> Episodes)?> TvmazeSupplement(string series,CancellationToken ct)
    {
        string externalField,externalId;
        if(provider=="tvdb"){externalField="thetvdb";externalId=series;}
        else {
            var external=await Request($"tv/{series}/external_ids",ct);
            if(external?.Int("id")!=int.Parse(series))throw new InvalidDataException("Series external ID mismatch.");
            externalField="imdb";externalId=external.Text("imdb_id");
            if(!Regex.IsMatch(externalId,@"^tt\d+$"))return null;
        }
        // Do not carry selected-provider credentials to a supplemental provider.
        if(http.DefaultRequestHeaders.Authorization!=null)throw new InvalidOperationException("Supplemental metadata requires request-scoped credentials.");
        var url=new Uri($"https://api.tvmaze.com/lookup/shows?{externalField}={Uri.EscapeDataString(externalId)}");
        using var response=await http.GetAsync(url,ct);
        JsonNode? show;
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        if(response.StatusCode==HttpStatusCode.MovedPermanently) {
            var redirect=response.Headers.Location;if(redirect==null)throw new InvalidDataException("Missing series redirect.");
            redirect=new Uri(url,redirect);
            if(redirect.Scheme!="https"||redirect.Host!="api.tvmaze.com"||!redirect.IsDefaultPort||redirect.UserInfo!=""||redirect.Query!=""||redirect.Fragment!=""||!Regex.IsMatch(redirect.AbsolutePath,@"^/shows/[1-9]\d*$"))
                throw new InvalidDataException("Unexpected metadata redirect.");
            using var target=await http.GetAsync(redirect,ct);Check(target);show=JsonNode.Parse(await target.Content.ReadAsStringAsync(ct));
        }else {Check(response);show=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));}
        if(show==null||show.Int("id")<=0||show["externals"]?[externalField]?.ToString()!=externalId)throw new InvalidDataException("Supplemental series external ID mismatch.");
        var id=show.Int("id").ToString();
        return (id,await new MetadataCatalog(http,"tvmaze").EpisodesAsync(id,ct));
    }
}

public static class DescriptionMapping
{
    internal static string Key(string title)=>Regex.Replace(title.ToLowerInvariant(),@"[^\p{L}\p{Nd}]","");
    internal static (string Title,int Part)? Part(string title) {
        var m=Regex.Match(title,@"^(.*?)\s*(?:\((?:part\s+)?(\d+)\)|[-:]?\s+part\s+(\d+))\s*$",RegexOptions.IgnoreCase);
        return m.Success&&int.TryParse(m.Groups[2].Success?m.Groups[2].Value:m.Groups[3].Value,out var number)&&number>0?(Key(m.Groups[1].Value),number):null;
    }
    public static Episode? Find(Episode episode,IReadOnlyList<Episode> primary,IReadOnlyList<Episode> supplemental) {
        var aligned=supplemental.Where(e=>e.Season==episode.Season&&e.Number==episode.Number).ToArray();
        if(aligned.Length!=1||primary.Count(e=>e.Season==episode.Season&&e.Number==episode.Number)!=1)return null;
        var candidate=aligned[0];
        if(Key(candidate.Title)==Key(episode.Title))return candidate;
        // Some providers omit part suffixes. Require a complete, consecutive,
        // position-aligned multipart group, never a title-only or number-only guess.
        if(Part(episode.Title) is not {} part||Key(candidate.Title)!=part.Title)return null;
        var group=primary.Where(e=>e.Season==episode.Season&&Part(e.Title)?.Title==part.Title).OrderBy(e=>e.Number).ToArray();
        var other=supplemental.Where(e=>e.Season==episode.Season&&Key(e.Title)==part.Title).OrderBy(e=>e.Number).ToArray();
        if(group.Length<2||group.Length!=other.Length)return null;
        for(int i=0;i<group.Length;i++)if(Part(group[i].Title)?.Part!=i+1||group[i].Number!=group[0].Number+i||other[i].Number!=group[i].Number)return null;
        return candidate;
    }
}
