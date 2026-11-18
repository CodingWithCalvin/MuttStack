using Scout.Core;
using System.Text.Json;

try
{
    if (args.Length == 0) throw new ArgumentException("Commands: prepare --scout-root ROOT --source DB --target DB --output NEW-DIRECTORY --settings-root ROOT --workspaces-root ROOT; apply --plan FILE; recover --plan FILE --action resume|rollback. Live paths require --allow-live.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal); var allowLive = false;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--allow-live") { if (allowLive) throw new ArgumentException("Duplicate --allow-live."); allowLive = true; continue; }
        if (!args[i].StartsWith("--") || i + 1 == args.Length || args[i + 1].StartsWith("--") || !options.TryAdd(args[i], args[++i])) throw new ArgumentException("Invalid or duplicate option.");
    }
    string Get(string key) => options.Remove(key, out var value) ? Path.GetFullPath(value) : throw new ArgumentException("Missing " + key);
    string Value(string key) => options.Remove(key, out var value) ? value : throw new ArgumentException("Missing " + key);
    void NoExtras() { if (options.Count != 0) throw new ArgumentException("Unknown option: " + options.Keys.First()); }
    if (args[0] == "status")
    {
        var root = Get("--scout-root"); NoExtras();
        Console.WriteLine(JsonSerializer.Serialize(Doggo.ScoutStorageSelection.Resolve(root), new JsonSerializerOptions { WriteIndented = true }));
    }
    else if (args[0] == "prepare")
    {
        var root = Get("--scout-root"); var source = Get("--source"); var target = Get("--target"); var output = Get("--output");
        var settings = Get("--settings-root"); var workspaces = Get("--workspaces-root"); NoExtras();
        var plan = ScoutDatabaseCutover.Prepare(root, source, target, output, settings, workspaces, allowLive);
        Console.WriteLine("Prepared and verified: " + plan); Console.WriteLine("No database selection changed. Review this plan before apply.");
    }
    else if (args[0] is "apply" or "recover")
    {
        var path = Get("--plan"); var action = args[0] == "apply" ? "resume" : Value("--action");
        var interruption = options.Remove("--interrupt-at", out var stage) ? stage : null;
        if (interruption != null && (allowLive || interruption is not ("before-import" or "during-import" or "after-import" or "before-selection" or "after-selection")))
            throw new ArgumentException("Interruption injection is limited to isolated tests and known checkpoints.");
        NoExtras();
        if (action == "rollback") { if (interruption != null) throw new ArgumentException("Rollback does not accept interruption injection."); ScoutDatabaseCutover.Rollback(path, allowLive); }
        else if (action == "resume") ScoutDatabaseCutover.Apply(path, allowLive, checkpoint => { if (checkpoint == interruption) throw new IOException("Simulated interruption at " + checkpoint + ". Run recover using this plan."); });
        else throw new ArgumentException("Recovery action must be resume or rollback.");
        var plan = ScoutDatabaseCutover.Load(path, allowLive);
        Console.WriteLine(File.ReadAllText(Path.Combine(plan.Directory, "cutover-result.json")));
    }
    else throw new ArgumentException("Unknown command.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
