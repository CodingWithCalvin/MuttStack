using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
namespace Tucker;

public static class MetadataSearch
{
    public static async Task<List<SeriesIdentity>> Search(string query,string databasePath,string provider,CancellationToken ct)
    {
        Validate(query,provider);
        var credential=Doggo.ScoutCredentialReader.Read(databasePath,provider);
        using var http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false}) {Timeout=TimeSpan.FromSeconds(30)};
        return await SearchAsync(http,query,provider,credential.Key,credential.Pin,ct);
    }
    public static async Task<List<SeriesIdentity>> SearchAsync(HttpClient http,string query,string provider,string key,string? pin=null,CancellationToken ct=default)
    {
        Validate(query,provider);
        if(string.IsNullOrWhiteSpace(key))throw new IOException($"Configure {provider.ToUpperInvariant()} credentials in Scout using the shared Mutt Stack database.");
        string root=provider=="tmdb"?"https://api.themoviedb.org/3/":"https://api4.thetvdb.com/v4/";
        string bearer=key;
        if(provider=="tvdb") {
            var login=new Dictionary<string,string>{{"apikey",key}};
            if(!string.IsNullOrWhiteSpace(pin))login["pin"]=pin;
            using var auth=await http.PostAsJsonAsync(root+"login",login,ct);Check(auth,provider);
            bearer=JsonNode.Parse(await auth.Content.ReadAsStringAsync(ct))?["data"]?["token"]?.ToString()??"";
            if(bearer.Length==0)throw new IOException("TVDB did not return a login token.");
        }
        var path=provider=="tmdb"?"search/tv?include_adult=false&language=en-US&query=":"search?type=series&limit=12&query=";
        using var request=new HttpRequestMessage(HttpMethod.Get,root+path+Uri.EscapeDataString(query.Trim()));
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);
        using var response=await http.SendAsync(request,ct);Check(response,provider);
        return ReadResults(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)),provider);
    }
    public static List<SeriesIdentity> ReadResults(JsonNode? body,string provider)
    {
        Validate("catalog",provider);
        var rows=body?[provider=="tmdb"?"results":"data"] as JsonArray??[];
        var results=new List<SeriesIdentity>();
        foreach(var row in rows) {
            if(row==null||provider=="tvdb"&&row["type"]?.ToString()!="series")continue;
            var id=row[provider=="tmdb"?"id":"tvdb_id"]?.ToString();var name=row["name"]?.ToString()??"";
            if(!long.TryParse(id,out var number)||number<=0||string.IsNullOrWhiteSpace(name))continue;
            var year=row[provider=="tmdb"?"first_air_date":"year"]?.ToString()??"";
            if(year.Length>4)year=year[..4];
            results.Add(new(provider,number.ToString(System.Globalization.CultureInfo.InvariantCulture),name,year,row["overview"]?.ToString()??""));
        }
        return results.DistinctBy(s=>s.Id).Take(12).ToList();
    }
    static void Validate(string query,string provider) {
        if(provider is not ("tmdb" or "tvdb"))throw new ArgumentException("Choose TMDB or TVDB.",nameof(provider));
        if(string.IsNullOrWhiteSpace(query)||query.Length>200)throw new IOException("Enter a series name (up to 200 characters).");
    }
    static void Check(HttpResponseMessage response,string provider) {
        if(!response.IsSuccessStatusCode)throw new IOException($"{provider.ToUpperInvariant()} returned HTTP {(int)response.StatusCode}. Check the saved credential and your connection.");
    }
}
