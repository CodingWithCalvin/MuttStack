using Ripley;
using System.Text.Json;
using System.Threading;
if(args.Length<4)throw new ArgumentException("history.json output-root doggo.db report.json [--apply expected-history-sha256]");
using var mutex=new Mutex(true,"Local\\Ripley.Desktop",out var owned);
if(!owned)throw new IOException("Close Ripley before reconciling its state.");
var report=CompletionReconciler.Inspect(args[0],args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
File.WriteAllText(args[3],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{report.Runs} completed runs; {report.Files} sources; {report.Completed.Count} verified outputs; {report.Issues.Count} issues. History SHA256 {report.HistorySha256}");
if(args.Length==6&&args[4]=="--apply"){CompletionReconciler.Apply(args[0],args[2],report,args[5]);Console.WriteLine("Completion ledger saved; history reconciled; no jobs queued.");}
if(report.Issues.Count>0)Environment.ExitCode=1;
