using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Doggo;
namespace Ripley;
public partial class MainWindow
{
 readonly System.Windows.Threading.DispatcherTimer handoffTimer=new(){Interval=TimeSpan.FromSeconds(5)};
 string HandoffDatabase=>Path.GetFullPath(stateRoot).Equals(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Ripley")),StringComparison.OrdinalIgnoreCase)?DoggoInbox.DefaultPath:Path.Combine(stateRoot,"doggo.db");
 void InitializeHandoffs(){RefreshHandoffs();handoffTimer.Tick+=(_,_)=>RefreshHandoffs();handoffTimer.Start();Closed+=(_,_)=>handoffTimer.Stop();}
 void RefreshHandoffs() {
  if(discBusy||adding)return;
  try {
   // Recover the local freeze before opening shared storage, which may be unavailable.
   var recovered=ripHistory.Where(s=>s.HandoffId.Length==0&&File.Exists(TuckerHandoff.ManifestPath(stateRoot,s.Id))).ToArray();
   foreach(var session in recovered) {
    session.HandoffId=Guid.Parse(session.Id).ToString("D");session.HandoffStatus="Not delivered";
    session.Detail="Handoff saved locally. Choose Retry send to Tucker to confirm delivery.";session.Refresh();
   }
   if(recovered.Length>0)SaveHandoffHistory();
   var inbox=new DoggoInbox(HandoffDatabase);bool changed=false;
   foreach(var session in ripHistory) {
    if(session.HandoffId.Length==0)continue;
    var status=inbox.Status(session.HandoffId);
    if(status==session.HandoffStatus)continue;
    session.HandoffStatus=status;if(status=="Accepted")session.HandoffAcceptedUtc??=DateTime.UtcNow;
    session.Detail=status switch {"Accepted"=>"Accepted by Tucker · run locked. Encoding is managed in Tucker.","Declined"=>"Tucker declined this handoff. Sources are kept; this run stays locked to prevent duplicate work.","Offered"=>"Sent to Tucker · awaiting inbox acceptance. No encode starts automatically.",_=>"Handoff saved locally. Choose Retry send to Tucker to deliver it."};session.Refresh();changed=true;
   }
   if(changed)SaveHandoffHistory();
  }catch(Exception ex){Status.Text="Could not refresh Tucker acknowledgement: "+ex.Message;}
 }
 void SaveHandoffHistory(){SaveHistory();if(currentRipSession?.HandoffId.Length>0)RipStatus.Text=currentRipSession.Detail;RefreshShell();}
 void SendToTucker(object sender,RoutedEventArgs e) {
  if(adding||discBusy||currentRipSession is not {} session||session.Archived||session.QueuedAtUtc.HasValue)return;
  if(session.HandoffStatus is "Offered" or "Accepted" or "Declined")return;
  SetAdding(true);
  try {
   if(session.Files.Any(f=>legacyQueuedSources.Contains(f.Source)))throw new IOException("This run has legacy compression records. Reconcile its previous work before sending to Tucker.");
   var id=TuckerHandoff.Prepare(stateRoot,JsonSerializer.SerializeToNode(session)!.AsObject());
   session.HandoffId=id;session.HandoffStatus="Not delivered";session.Detail="Handoff saved locally; awaiting delivery to Tucker.";
   // Persist the freeze before delivery so an offered run cannot be submitted twice.
   QueueStore.Write(HistoryPath,new HistoryDocument(currentRipSession?.Id,ripHistory.ToList()));
   TuckerHandoff.Deliver(stateRoot,id,HandoffDatabase);
   session.HandoffStatus="Offered";session.Detail="Sent to Tucker · awaiting inbox acceptance. No encode starts automatically.";
   QueueStore.Write(HistoryPath,new HistoryDocument(currentRipSession?.Id,ripHistory.ToList()));
   RipStatus.Text=Status.Text=session.Detail;session.Refresh();
  }catch(Exception ex){Error(ex);}
  finally{SetAdding(false);RefreshHandoffs();RefreshShell();}
 }
}
