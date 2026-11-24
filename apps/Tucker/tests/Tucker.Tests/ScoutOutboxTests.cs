using Doggo;
using Tucker;

static class ScoutOutboxTests
{
    public static int Run(string temp)
    {
        var root = Path.Combine(temp, "scout-outbox"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "doggo.db"); var legacy = new DoggoDatabase(path);
        var outbox = new ScoutOutbox(path); var transport = new RoutedHandoffs(path);
        var passed = 0;
        void Check(bool yes, string label) { if (!yes) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
        void Reject(Action action, string label) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { Check(true, label); return; } throw new Exception(label); }
        Job Job(string name, string status = "Completed")
        {
            var source = Path.Combine(root, "raw-" + name + ".mkv"); File.WriteAllText(source, "raw");
            var output = Path.Combine(root, name + ".mkv"); File.WriteAllText(output, "synthetic compressed " + name);
            return new() { QueueId = "test-run", QueueName = "Example Show", Source = source, Name = Path.GetFileName(output),
                Encoding = new("", "", "", false, root, "mkv"), Status = status, Series = new("tvdb", "42", "Example Show", "2020", "Overview"),
                Season = "02", VolumeNumber = "1", DiscNumber = "3", DiscLabel = "Example S2 D3", OriginalFilename = "title00.mkv", RipDate = new(2026, 1, 2) };
        }
        var first = Job("first"); var waiting = Job("waiting", "Waiting"); var running = Job("running", "Encoding");
        var id = outbox.SendCompleted("test-run", [first, waiting, running])!;
        var batch = transport.Inbox("scout").Single().Read();
        Check(batch.Files.Count == 1 && batch.Files[0].Path == first.OutputPath && batch.Files[0].Path != first.Source, "Scout handoff includes completed compressed outputs only");
        Check(batch.Files[0] is { Season: "02", Disc: "3", Volume: "1", OriginalFilename: "title00.mkv" } && batch.Files[0].Series?.Overview == "Overview", "Scout handoff preserves per-file series and disc provenance");
        Check(legacy.PendingInboxCount() == 0 && transport.Inbox("tucker").Count == 0, "Legacy Tucker inbox and other recipients cannot see Scout work");
        Check(new ScoutOutbox(path).SendCompleted("test-run", [first]) == null && transport.Inbox("scout").Count == 1, "Sender restart and repeat send keep one immutable batch");
        var frozen = transport.Inbox("scout").Single();
        transport.Offer(frozen.Id, frozen.Digest, frozen.Payload);
        using var sql = new Sqlite(path);
        Check(sql.Query("SELECT kind FROM handoff_routed_events WHERE batch_id=?", id).Count == 1, "Retrying delivery records one offered event");
        Reject(() => transport.Offer(id, "wrong", frozen.Payload), "Transport rejects a mismatched payload digest");
        var altered = (batch with { RunName = "Changed" }).Serialize();
        Reject(() => transport.Offer(id, ScoutHandoff.Digest(altered), altered), "Transport rejects reuse of an ID with different contents");
        var badRoute = (batch with { Id = Guid.NewGuid().ToString("N"), TargetApp = "tucker" }).Serialize();
        Reject(() => transport.Offer(System.Text.Json.JsonSerializer.Deserialize<ScoutHandoff>(badRoute)!.Id, ScoutHandoff.Digest(badRoute), badRoute), "Transport rejects incompatible recipients");
        Reject(() => transport.Decline(id, "ripley"), "Another app cannot decline Scout's handoff");
        var malformed = (batch with { Id = Guid.NewGuid().ToString("N"), Files = [batch.Files[0] with { Series = batch.Files[0].Series! with { Year = null! } }] }).Serialize();
        Reject(() => ScoutHandoff.Read(System.Text.Json.JsonSerializer.Deserialize<ScoutHandoff>(malformed)!.Id, ScoutHandoff.Digest(malformed), malformed), "Null series metadata is rejected before it can break Scout's saved identity");
        waiting.Status = "Completed";
        Check(outbox.SendCompleted("test-run", [first, waiting]) != id && transport.Inbox("scout").Count == 2, "Later completed additions create a new batch without resending earlier outputs");
        transport.Decline(id, "scout"); transport.Decline(id, "scout");
        Check(new ScoutOutbox(path).Deliveries("test-run").Single(d => d.Id == id).Status == "Declined", "Closed Tucker learns Scout's durable decline after reopening");
        var missing = Job("missing"); File.Delete(missing.OutputPath);
        Reject(() => outbox.SendCompleted("test-run", [missing]), "Missing completed output is not offered");
        var raw = Job("raw"); raw.Name = Path.GetFileName(raw.Source);
        Reject(() => outbox.SendCompleted("test-run", [raw]), "Raw source cannot be offered as compressed output");
        // Interrupt between durable outbox commit and transport delivery.
        sql.Exec("CREATE TRIGGER fail_delivery BEFORE INSERT ON handoff_routed_batches BEGIN SELECT RAISE(ABORT,'delivery interrupted'); END");
        var late = Job("late");
        Reject(() => outbox.SendCompleted("test-run", [late]), "Interrupted delivery surfaces a recoverable error");
        var pending = outbox.Deliveries("test-run").Single(d => d.Status == "Not delivered");
        var payload = sql.Query("SELECT payload FROM tucker_scout_outbox WHERE id=?", pending.Id)[0][0];
        late.Series = null; // Changed in-memory job must not change the committed offer.
        sql.Exec("DROP TRIGGER fail_delivery"); new ScoutOutbox(path).RetryPending();
        Check(transport.Inbox("scout").Single(b => b.Id == pending.Id).Payload == payload, "Recovery delivers the original frozen payload and ID");
        legacy.Save(new QueueState());
        Check(new ScoutOutbox(path).Deliveries("test-run").Count == 3, "Deleting Tucker runs retains handoff receipts and retry records");
        var future = Path.Combine(root, "future.db"); _ = new DoggoDatabase(future);
        using var futureSql = new Sqlite(future); futureSql.Exec("INSERT INTO doggo_migrations VALUES('handoff_routes',2,'future')");
        Reject(() => new ScoutOutbox(future), "Future handoff schema fails closed without changing legacy component versions");
        Check(sql.Query("SELECT MAX(version) FROM doggo_migrations WHERE component='handoff'")[0][0] == "1", "Recipient-aware handoffs preserve the legacy protocol version");
        var concurrent = Job("concurrent");
        Task.WhenAll(Task.Run(() => new ScoutOutbox(path).SendCompleted("test-run", [concurrent])),
            Task.Run(() => new ScoutOutbox(path).SendCompleted("test-run", [concurrent]))).GetAwaiter().GetResult();
        Check(transport.Inbox("scout").Count(b => b.Read().Files.Any(f => f.JobId == concurrent.Id)) == 1, "Concurrent sends reserve a completed output only once");
        return passed;
    }
}
