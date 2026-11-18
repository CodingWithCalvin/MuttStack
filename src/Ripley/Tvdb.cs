using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Ripley;
public record SeriesIdentity(string Provider, string Id, string Name, string Year, string Overview)
{
    public string Display => $"{Name} ({Year}) · TVDB {Id}";
}
public static class Tvdb
{
    public static async Task<List<SeriesIdentity>> Search(string query, string scoutFolder, CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new IOException("Enter a series name (up to 200 characters).");
        var (key,pin) = ScoutCredentials.Read(scoutFolder);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        const string root = "https://api4.thetvdb.com/v4/";
        var login = new Dictionary<string,string> { ["apikey"] = key }; if(!string.IsNullOrWhiteSpace(pin)) login["pin"] = pin;
        using var auth = await http.PostAsJsonAsync(root + "login", login, token);
        Check(auth);
        var authBody = JsonNode.Parse(await auth.Content.ReadAsStringAsync(token));
        var bearer = authBody?["data"]?["token"]?.ToString();
        if(string.IsNullOrEmpty(bearer)) throw new IOException("TVDB did not return a login token.");
        using var request = new HttpRequestMessage(HttpMethod.Get,root + "search?type=series&limit=12&query=" + Uri.EscapeDataString(query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",bearer);
        using var response = await http.SendAsync(request,token); Check(response);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
        return ReadResults(body);
    }
    public static List<SeriesIdentity> ReadResults(JsonNode? body) => (body?["data"] as JsonArray ?? new()).Where(n=>n?["type"]?.ToString() == "series" && long.TryParse(n["tvdb_id"]?.ToString(),out var id) && id > 0).Select(n=>new SeriesIdentity("tvdb",n!["tvdb_id"]!.ToString(),n["name"]?.ToString()??"",n["year"]?.ToString()??"",n["overview"]?.ToString()??"")).ToList();
    static void Check(HttpResponseMessage response) { if(!response.IsSuccessStatusCode) throw new IOException($"TVDB returned HTTP {(int)response.StatusCode}. Check Scout's TVDB key/PIN and your connection."); }
}
// Both companions follow Scout's authoritative storage choice.
public static class ScoutCredentials
{
    public static (string Key,string? Pin) Read(string folder)=>Doggo.ScoutCredentialReader.ReadTvdb(folder);
}
