using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Scout.Core;

static class DescriptionFixture
{
    public static async Task Prepare(string input,string output)
    {
        if(File.Exists(output))throw new IOException("The isolated fixture already exists; it will not be replaced.");
        var fixture=JsonNode.Parse(await File.ReadAllTextAsync(input))!.AsObject();
        var identity=fixture["catalog"]!["series_identity"]!.Deserialize<SeriesIdentity>(Data.Options)!;identity.Validate();
        var original=fixture["catalog"]!["episodes"]!.Deserialize<List<Episode>>(Data.Options)!;
        var keys=new Dictionary<string,string>();
        // The existing frozen snapshot supplies credentials only in memory. No
        // live settings/database is opened, and no key or token is saved/logged.
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=fixture.Text("source_snapshot"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString())) {
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT content FROM documents WHERE kind='credentials.dpapi'";
            using var rows=cmd.ExecuteReader();
            if(rows.Read()) {
                try{keys=JsonSerializer.Deserialize<Dictionary<string,string>>(WindowsSecrets.Unprotect((byte[])rows[0]))??[];}
                catch(CryptographicException){Console.WriteLine("Saved provider credentials could not be unlocked. Verified public supplemental metadata remains available.");}
                if(rows.Read())throw new InvalidDataException("Multiple credential documents in the isolated snapshot.");
            }
        }
        var directory=Path.GetDirectoryName(Path.GetFullPath(output))!;Directory.CreateDirectory(directory);
        var guide=Path.Combine(directory,"catalog.json");if(File.Exists(guide))throw new IOException("Use a new isolated fixture directory.");
        Data.Save(guide,fixture["catalog"]!);
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60)};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout/0.9");
        var provider=new MetadataCatalog(http,identity.Provider,keys.GetValueOrDefault("metadata|"+identity.Provider),keys.GetValueOrDefault("metadata|tvdb-pin"));
        var recovered=await EpisodeGuidePreparation.EnsureAsync(guide,identity,provider,default);
        if(!original.Select(e=>(e.Id,e.Season,e.Number,e.Title)).SequenceEqual(recovered.Select(e=>(e.Id,e.Season,e.Number,e.Title))))
            throw new InvalidDataException("Description preparation changed authoritative episode identities.");
        fixture["catalog"]=Data.Read(guide);fixture["title"]="Farpoint · automatic description recovery";
        fixture["description_recovery"]=new JsonObject{["original_fixture"]=Path.GetFullPath(input),["configured_provider"]=identity.Provider,
            ["original_missing"]=original.Count(e=>string.IsNullOrWhiteSpace(e.Overview)),["remaining_missing"]=recovered.Count(e=>string.IsNullOrWhiteSpace(e.Overview)),
            ["sources"]=JsonSerializer.SerializeToNode(recovered.Where(e=>e.DescriptionSource!=null).Select(e=>new{e.Id,e.Code,e.Title,e.DescriptionSource,e.DescriptionIssue}),Data.Options)};
        fixture["limitations"]="One development case with saved dialogue and automatically retrieved provider descriptions. No independently confirmed assignment label. This is not a general accuracy benchmark.";
        await File.WriteAllTextAsync(output,fixture.ToJsonString(Data.Options));
        Console.WriteLine($"Descriptions retrieved: {original.Count(e=>string.IsNullOrWhiteSpace(e.Overview))-recovered.Count(e=>string.IsNullOrWhiteSpace(e.Overview))}; still missing: {recovered.Count(e=>string.IsNullOrWhiteSpace(e.Overview))}. Authoritative IDs, titles, numbering and saved dialogue unchanged.");
        foreach(var episode in recovered.Where(e=>e.DescriptionSource!=null))Console.WriteLine($"{episode.Code} · {episode.Title}: {episode.DescriptionSource!.Provider} ({episode.DescriptionSource.Url})");
    }
}
