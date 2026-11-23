namespace Scout.Core.Subtitles;

internal static class ReferenceTitles {
 public static ProviderLink? Find(SubtitleReference reference,IReadOnlyList<Episode> catalog,out bool conflict) {
  var links=new[]{reference.Name}.Concat(reference.TitleNames??[]).Select(n=>TitleMapping.Find(n,catalog,reference.Id)).OfType<ProviderLink>().ToArray();
  conflict=links.Select(l=>l.Key).Distinct().Count()>1||links.Select(l=>l.Complete).Distinct().Count()>1;
  return conflict?null:links.FirstOrDefault();
 }
}
