namespace Scout.Core;

public interface IExternalSeriesLookup
{
    Task<SeriesIdentity?> FindTvdbAsync(string tvdbId,CancellationToken ct);
}

public sealed partial class MetadataCatalog : IExternalSeriesLookup
{
    public async Task<SeriesIdentity?> FindTvdbAsync(string tvdbId,CancellationToken ct) {
        if(provider!="tmdb")return null;
        if(!long.TryParse(tvdbId,out var id)||id<=0)throw new ArgumentException("Invalid TVDB series ID.");
        var found=await Request($"find/{id}?external_source=tvdb_id&language=en-US",ct);
        var rows=found.Array("tv_results");
        if(rows.Count!=1)return null;
        var identity=Read(rows[0]);
        if(identity==null)return null;
        var external=await Request($"tv/{identity.Id}/external_ids",ct);
        if(external?["id"]?.ToString()!=identity.Id||external?["tvdb_id"]?.ToString()!=id.ToString())
            throw new InvalidDataException("TMDB did not confirm the original TVDB series identity.");
        return identity;
    }
}
