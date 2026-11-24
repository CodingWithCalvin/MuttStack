using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Scout.Core.Subtitles;

internal static class SubdlIdentity {
 // Title search returns "sd123" while subtitle search can return numeric 123.
 // Canonicalize only this documented ID format; malformed IDs never match.
 public static string? Normalize(JsonNode? value) {
  if(value is not JsonValue)return null;
  var text=value.ToString().Trim();
  if(!Regex.IsMatch(text,@"^(?:sd)?[0-9]{1,20}$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))return null;
  if(text.StartsWith("sd",StringComparison.OrdinalIgnoreCase))text=text[2..];
  text=text.TrimStart('0');return text.Length==0?null:text;
 }
 public static bool Matches(JsonNode? result,string id,string imdb,SeriesIdentity series) {
  if(result is not JsonObject)return false;
  if(result["type"]!=null&&!string.Equals(result["type"]!.ToString(),"tv",StringComparison.OrdinalIgnoreCase))return false;
  if(result["sd_id"]!=null&&Normalize(result["sd_id"])!=id)return false;
  if(result["imdb_id"]!=null&&result["imdb_id"]!.ToString()!=imdb)return false;
  if(series.Provider=="tmdb"&&result["tmdb_id"]!=null&&result["tmdb_id"]!.ToString()!=series.Id)return false;
  return true;
 }
}
