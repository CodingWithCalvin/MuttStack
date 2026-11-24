using System.Text.Json.Nodes;
namespace Scout.Core;

public static class DestinationChoices {
 static string Key(JsonObject row,string destination)=>CollectionResolver.Hash(Path.GetFullPath(row.Text("original_path",row.Text("path"))).ToLowerInvariant()+"|"+
  CollectionResolver.FingerprintKey(row["primary_fingerprint"])+"|"+Path.GetFullPath(destination).ToLowerInvariant());
 public static void Save(JsonObject report,MovePlan plan,string source,string choice) {
  MoveExecutor.RequireSettled(report);
  if(choice is not ("both" or "skip" or "reset"))throw new ArgumentException("Unknown destination choice.");
  var item=plan.Items.Single(i=>PreviewPlanner.Same(i.Source,source)&&i.Action!="completed");
  var canonical=item.CanonicalDestination??item.Destination??throw new InvalidOperationException("This file has no proposed destination.");
  var row=report.Array("files").OfType<JsonObject>().Single(r=>PreviewPlanner.Same(r.Text("path"),source));
  if(CollectionResolver.FingerprintKey(row["primary_fingerprint"])==null)throw new InvalidOperationException("Identify this recording before choosing a destination.");
  if(report["destination_choices"] is not JsonObject choices)report["destination_choices"]=choices=new();
  var key=Key(row,canonical);
  if(choice=="reset"){choices.Remove(key);return;}
  if(!item.DestinationConflict&&item.DestinationChoice.Length==0)throw new InvalidOperationException("This file has no destination conflict to resolve.");
  string? target=null;
  if(choice=="both") {
   var reserved=plan.Items.Where(i=>i.Destination!=null).Select(i=>i.Destination!).ToHashSet(StringComparer.OrdinalIgnoreCase);
   var folder=Path.GetDirectoryName(canonical)!;var name=Path.GetFileNameWithoutExtension(canonical);var extension=Path.GetExtension(canonical);
   for(int n=2;n<10000;n++) {
    var candidate=Path.Combine(folder,$"{name} ({n}){extension}");
    if(!reserved.Contains(candidate)&&!File.Exists(candidate)&&!Directory.Exists(candidate)){target=candidate;break;}
   }
   if(target==null)throw new IOException("No free destination filename found.");
  }
  choices[key]=new JsonObject{["choice"]=choice,["destination"]=target};
 }
 internal static void Apply(JsonObject report,List<PlanItem> items,string destinationRoot) {
  if(report["destination_choices"] is not JsonObject choices)return;
  var rows=report.Array("files").OfType<JsonObject>().ToDictionary(r=>Path.GetFullPath(r.Text("path")),StringComparer.OrdinalIgnoreCase);
  foreach(var item in items.Where(i=>i.Destination!=null)) {
   item.CanonicalDestination=item.Destination;
   var saved=choices[Key(rows[item.Source],item.Destination!)];
   if(saved.Text("choice")=="skip") {
    item.DestinationChoice="skip";item.Action="hold";item.Change="Leave in place";item.Notes.Add("You chose to leave this source in place. Reset the destination choice to reconsider.");
   } else if(saved.Text("choice")=="both"&&saved.Text("destination") is {Length:>0} target) {
    item.DestinationChoice="both";
    if(!Path.IsPathFullyQualified(target)||!PreviewPlanner.Within(target,destinationRoot)||!PreviewPlanner.Same(Path.GetDirectoryName(target)!,Path.GetDirectoryName(item.Destination!)!)||PreviewPlanner.HasReparseAncestor(target)) {
     item.BlockedReasons.Add("Saved destination choice is outside the proposed folder or crosses a junction. Reset the destination choice.");item.Action="hold";continue;
    }
    item.Destination=target;
    item.Action=item.BlockedReasons.Count>0?"hold":PreviewPlanner.Same(item.Source,target)?"unchanged":"rename/move";
    item.Notes.Add("You chose Keep both. Review the new filename, then select this move to apply it.");
   }
  }
 }
}
