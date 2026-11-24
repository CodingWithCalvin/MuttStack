namespace Scout.Core;

public static class EpisodeGuidePreparation
{
    public static async Task<List<Episode>> EnsureAsync(string path,SeriesIdentity? identity,IMetadataCatalog? provider,CancellationToken ct) {
        bool saved=ScoutStorage.Exists(path);
        if(!saved&&(identity==null||provider==null))throw new InvalidOperationException("Select a show or import an episode guide before identifying episodes.");
        identity?.Validate();
        if(identity!=null&&provider!=null&&provider.Provider!=identity.Provider)throw new InvalidDataException("The metadata client must match the selected provider.");
        var episodes=saved?(identity==null?Data.Catalog(path):SavedGuide.Episodes(path,identity)):await provider!.EpisodesAsync(identity!.Id,ct);
        if(episodes.Count==0||episodes.Select(e=>e.Id).Distinct().Count()!=episodes.Count||episodes.Any(e=>string.IsNullOrWhiteSpace(e.Title)||e.Season<0||e.Number<0))
            throw new InvalidDataException("The provider did not return a valid episode guide.");
        var original=episodes;
        if(identity!=null&&provider is IEpisodeDescriptions descriptions)
            episodes=await descriptions.CompleteDescriptionsAsync(identity.Id,episodes,ct);
        ct.ThrowIfCancellationRequested();
        if(!saved)Data.Save(path,new{series_identity=identity,episodes});
        else if(!episodes.SequenceEqual(original)) {
            var document=Data.Read(path);document["episodes"]=System.Text.Json.JsonSerializer.SerializeToNode(episodes,Data.Options);Data.Save(path,document);
        }
        return episodes;
    }
}
