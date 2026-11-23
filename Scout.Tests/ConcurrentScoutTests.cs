using Scout.Core;

static class ConcurrentScoutTests {
 static void Check(bool value,string message="Concurrent Scout regression"){if(!value)throw new Exception(message);}
 public static void Run(Action<string,Action> test,string temp) {
  test("Two Scout workers overlap and a third waits until a slot is released",()=>Run(async()=>{
   var scheduler=new ScoutRunCoordinator();var pause=new ScoutPause();
   using var first=await scheduler.EnterWorkerAsync(pause,default);var second=await scheduler.EnterWorkerAsync(pause,default);
   var third=scheduler.EnterWorkerAsync(pause,default);Check(!third.IsCompleted);
   second.Dispose();using var next=await third.WaitAsync(TimeSpan.FromSeconds(3));
  }));
  test("Pausing a queued Scout does not stop either active worker",()=>Run(async()=>{
   var scheduler=new ScoutRunCoordinator();using var first=await scheduler.EnterWorkerAsync(new(),default);using var second=await scheduler.EnterWorkerAsync(new(),default);
   var pause=new ScoutPause();var queued=scheduler.EnterWorkerAsync(pause,default);pause.Request();
   try{using var bad=await queued.WaitAsync(TimeSpan.FromSeconds(3));throw new Exception("Queued Scout ignored pause");}catch(ScoutPausedException){}
   var later=scheduler.EnterWorkerAsync(new(),default);Check(!later.IsCompleted);second.Dispose();using var lease=await later.WaitAsync(TimeSpan.FromSeconds(3));
  }));
  test("Cancelling a model waiter leaves the model owner and later waiters intact",()=>Run(async()=>{
   var scheduler=new ScoutRunCoordinator();var owner=await scheduler.EnterModelAsync(null,default);using var cancel=new CancellationTokenSource();
   var waiting=scheduler.EnterModelAsync(null,cancel.Token);Check(!waiting.IsCompleted);cancel.Cancel();
   try{using var bad=await waiting;throw new Exception("Model cancellation ignored");}catch(OperationCanceledException){}
   var next=scheduler.EnterModelAsync(null,default);Check(!next.IsCompleted);owner.Dispose();using var held=await next.WaitAsync(TimeSpan.FromSeconds(3));owner.Dispose();
   var pause=new ScoutPause();var stillWaiting=scheduler.EnterModelAsync(pause,default);Check(!stillWaiting.IsCompleted);pause.Request();
   try{using var bad=await stillWaiting;throw new Exception("Model pause ignored");}catch(ScoutPausedException){}
  }));
  test("Active and queued source reservations reject overlapping roots and conflicting moves",()=>{
   var scheduler=new ScoutRunCoordinator();var source=Path.Combine(temp,"reserved-source");
   using(var held=scheduler.Reserve("one",source)) {
    foreach(var root in new[]{source,Path.Combine(source,"Season 01"),temp}) {
     try{using var bad=scheduler.Reserve("two",root);throw new Exception("Overlap accepted");}catch(InvalidOperationException){}
     try{scheduler.CheckPaths([Path.Combine(root,"episode.mkv"),source]);throw new Exception("Conflicting move accepted");}catch(InvalidOperationException){}
    }
    using var separate=scheduler.Reserve("two",source+"-different");Check(scheduler.Count==2);
   }
   using var reopened=scheduler.Reserve("one",source);Check(scheduler.Count==1);
  });
  test("Queued Scouts recover as interrupted and cannot be archived or deleted",()=>{
   var store=new WorkspaceStore(Path.Combine(temp,"queued-recovery"));var queued=new RunWorkspace{Status="Queued",Library=temp};store.Save(queued);
   try{store.ArchiveRun(queued);throw new Exception("Queued archive accepted");}catch(InvalidOperationException){}
   try{store.DeleteRun(queued);throw new Exception("Queued delete accepted");}catch(InvalidOperationException){}
   Check(store.ReadAll(true).Single().Status=="Interrupted");
  });
 }
 static void Run(Func<Task> action)=>action().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
}
