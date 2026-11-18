using Doggo;
using Scout.Core;

static class HandoffTests
{
    public static void Run(Action<string, Action> test, string temp)
    {
        void Scenario(string name, Action<ScoutInbox, RoutedHandoffs, WorkspaceStore, string> action) => test(name, () =>
        {
            var root = Path.Combine(temp, "handoff-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var db = new ScoutDatabase(Path.Combine(root, "doggo.db"), true);
            var store = new WorkspaceStore(Path.Combine(root, "workspaces")); ScoutStorage.Register(store.Root, db);
            try { action(new(db, store.Root), new(db.Path), store, root); }
            finally { ScoutStorage.Unregister(store.Root); }
        });
        static void Check(bool ok) { if (!ok) throw new Exception("Handoff assertion failed."); }
        static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { return; } throw new Exception("Invalid acceptance succeeded."); }
        static PublishedOutput FileAt(string root, string name, HandoffSeries? series = null)
        {
            var path = Path.Combine(root, name + ".mkv"); File.WriteAllText(path, "synthetic compressed video " + name); var stat = new FileInfo(path);
            return new(Guid.NewGuid().ToString("N"), path, stat.Length, stat.LastWriteTimeUtc, series, "2", "1", "3", "S2 D3");
        }
        static string Offer(RoutedHandoffs transport, params PublishedOutput[] files)
        {
            var batch = new ScoutHandoff(Guid.NewGuid().ToString("N"), "run", "From Tucker", files.ToList()); var payload = batch.Serialize();
            transport.Offer(batch.Id, ScoutHandoff.Digest(payload), payload); return batch.Id;
        }
        Scenario("Handoff acceptance splits series into exact saved file selections and does not start processing", (inbox, transport, store, root) =>
        {
            var a = FileAt(root, "a", new("tvdb", "12", "Alpha", "2020", "Overview"));
            var b = FileAt(root, "b", new("tmdb", "12", "Beta")); var c = FileAt(root, "c");
            FileAt(root, "unrelated");
            var id = Offer(transport, a, b, c); var receipt = inbox.Accept(id); var runs = store.ReadAll();
            Check(receipt.NewFiles == 3 && runs.Count == 3 && runs.All(r => r.Status == "Draft" && r.SelectionScope?.Kind == "files" && r.SelectedFiles.Count == 1 && r.Report == ""));
            Check(runs.Single(r => r.SeriesIdentity?.Provider == "tvdb").SeriesIdentity?.Overview == "Overview");
            Check(runs.Single(r => r.SeriesIdentity == null).Setup?.Mode == "discover");
            Check(runs.SelectMany(r => r.SelectedFiles).Order().SequenceEqual(new[] { a.Path, b.Path, c.Path }.Order()));
            Check(runs.All(r => VideoSelection.DiscoveryCandidates(r).SequenceEqual(r.SelectedFiles)));
            Check(transport.Inbox("scout").Single().Status == "Accepted");
        });
        Scenario("Receipt survives restart, deleted workspace and subsequently moved media", (inbox, transport, store, root) =>
        {
            var file = FileAt(root, "move"); var id = Offer(transport, file); var first = inbox.Accept(id);
            var run = store.ReadAll().Single(); store.DeleteRun(run); File.Move(file.Path, file.Path + ".moved");
            var reopened = new ScoutInbox(ScoutDatabase.OpenExistingDoggo(transport.Path), store.Root).Accept(id);
            Check(first.RunIds.SequenceEqual(reopened.RunIds) && store.ReadAll().Count == 0);
            using var db = new Sqlite(transport.Path); Check(db.Query("SELECT kind FROM handoff_routed_events WHERE kind='Accepted'").Count == 1);
        });
        Scenario("Overlapping handoffs deduplicate outputs across batches", (inbox, transport, store, root) =>
        {
            var a = FileAt(root, "a"); var b = FileAt(root, "b"); inbox.Accept(Offer(transport, a));
            var second = inbox.Accept(Offer(transport, a with { JobId = "other-job" }, b));
            Check(second.NewFiles == 1 && second.RunIds.Count == 2 && store.ReadAll().SelectMany(r => r.SelectedFiles).Count() == 2);
        });
        Scenario("Duplicate output with conflicting metadata leaves handoff offered", (inbox, transport, store, root) =>
        {
            var a = FileAt(root, "a", new("tvdb", "12", "Alpha")); inbox.Accept(Offer(transport, a));
            var id = Offer(transport, a with { Series = new("tvdb", "13", "Wrong") }); Reject(() => inbox.Accept(id));
            Check(store.ReadAll().Count == 1 && transport.Inbox("scout").Single(b => b.Id == id).Status == "Offered");
        });
        Scenario("Missing or changed outputs reject the whole batch without receipts", (inbox, transport, store, root) =>
        {
            var a = FileAt(root, "a"); var b = FileAt(root, "b"); var id = Offer(transport, a, b); File.AppendAllText(b.Path, "changed");
            Reject(() => inbox.Accept(id)); File.Delete(b.Path); Reject(() => inbox.Accept(id));
            Check(store.ReadAll().Count == 0 && transport.Inbox("scout").Single().Status == "Offered");
            using var db = new Sqlite(transport.Path); Check(db.Query("SELECT * FROM scout_handoff_receipts").Count == 0);
        });
        Scenario("Acceptance transaction rolls back workspaces, receipt, file ledger and status on failure", (inbox, transport, store, root) =>
        {
            var id = Offer(transport, FileAt(root, "a")); using var db = new Sqlite(transport.Path);
            db.Exec("CREATE TRIGGER fail_accept BEFORE INSERT ON handoff_routed_events WHEN NEW.kind='Accepted' BEGIN SELECT RAISE(ABORT,'simulated crash'); END");
            Reject(() => inbox.Accept(id));
            Check(store.ReadAll().Count == 0 && db.Query("SELECT * FROM scout_handoff_receipts").Count == 0 && db.Query("SELECT * FROM scout_handoff_files").Count == 0 && transport.Inbox("scout").Single().Status == "Offered");
            db.Exec("DROP TRIGGER fail_accept"); Check(inbox.Accept(id).NewFiles == 1);
        });
        Scenario("Scout ignores wrong-recipient and future-contract rows and refuses tampered payloads", (inbox, transport, store, root) =>
        {
            var id = Offer(transport, FileAt(root, "a")); using var db = new Sqlite(transport.Path);
            db.Exec("UPDATE handoff_routed_batches SET target_app='ripley' WHERE id=?", id); Check(inbox.Items().Count == 0); Reject(() => inbox.Accept(id));
            db.Exec("UPDATE handoff_routed_batches SET target_app='scout',version=2 WHERE id=?", id); Check(inbox.Items().Count == 0); Reject(() => inbox.Accept(id));
            db.Exec("UPDATE handoff_routed_batches SET version=1,payload='{}' WHERE id=?", id); Reject(() => inbox.Accept(id)); Check(store.ReadAll().Count == 0);
        });
        Scenario("Decline is durable, idempotent and cannot overwrite acceptance", (inbox, transport, store, root) =>
        {
            var id = Offer(transport, FileAt(root, "a")); inbox.Decline(id); inbox.Decline(id); Reject(() => inbox.Accept(id));
            Check(transport.Inbox("scout").Single().Status == "Declined" && store.ReadAll().Count == 0);
            var accepted = Offer(transport, FileAt(root, "b")); inbox.Accept(accepted); Reject(() => inbox.Decline(accepted));
        });
        test("Standalone Scout cannot initialize a shared inbox", () =>
        {
            var root = Path.Combine(temp, "standalone-inbox"); var db = new ScoutDatabase(Path.Combine(root, "scout.db"));
            Reject(() => new ScoutInbox(db, root)); using var sql = new Sqlite(db.Path);
            Check(sql.Query("SELECT name FROM sqlite_master WHERE name LIKE '%handoff%'").Count == 0);
        });
        Scenario("Concurrent acceptance creates one receipt and one saved Scout", (inbox, transport, store, root) =>
        {
            var id = Offer(transport, FileAt(root, "concurrent"));
            var results = Task.WhenAll(Task.Run(() => inbox.Accept(id)), Task.Run(() => inbox.Accept(id))).GetAwaiter().GetResult();
            Check(results[0].RunIds.SequenceEqual(results[1].RunIds) && store.ReadAll().Count == 1);
            using var db = new Sqlite(transport.Path); Check(db.Query("SELECT * FROM handoff_routed_events WHERE kind='Accepted'").Count == 1);
        });
        Scenario("Newer Scout handoff schema is refused", (inbox, transport, store, root) =>
        {
            using var db = new Sqlite(transport.Path); db.Exec("INSERT INTO doggo_migrations VALUES('scout_handoffs',2,'future')");
            Reject(() => new ScoutInbox(ScoutDatabase.OpenExistingDoggo(transport.Path), store.Root));
        });
    }
}
