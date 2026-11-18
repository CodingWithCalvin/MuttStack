using Scout.Core;
using System.Text.Json;
if(args.Length!=3){Console.Error.WriteLine("Usage: Scout.DatabaseRehearsal <scout.db> <doggo.db or -> <new-output-directory>");return 2;}
try {var report=ScoutDoggoMigration.Rehearse(args[0],args[1]=="-"?null:args[1],args[2]);Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return 0;}
catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
