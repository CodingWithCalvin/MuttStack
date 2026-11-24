using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class ModelActivityTests
{
    static void Check(bool yes) { if (!yes) throw new Exception("Model activity assertion failed."); }
    static HttpResponseMessage Result() => new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"ok\":true}"}}],"usage":{"prompt_tokens":12,"completion_tokens":3}}""") };
    public static void Run(Action<string, Action> test)
    {
        test("Model readiness is reported only after a successful health response", () => {
            var messages = new List<string>(); var attempts = 0;
            using var http = new HttpClient(new Handler((_, _) => {
                Check(messages.All(m => !m.Contains("ready")));
                return Task.FromResult(new HttpResponseMessage(++attempts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
            }));
            LocalRuntime.WaitUntilReadyAsync(http, new("http://127.0.0.1/health"), () => false, new Observer<string>(messages.Add), default, TimeSpan.Zero).GetAwaiter().GetResult();
            Check(attempts == 2 && messages.SequenceEqual(new[] { "Loading local model…", "Local model ready. Starting analysis…" }));
        });
        test("Cancelled or exited runtimes never report model ready", () => {
            foreach (var cancelled in new[] { false, true }) {
                var messages = new List<string>(); using var http = new HttpClient(new Handler((_, _) => throw new Exception("Unexpected health request")));
                try { LocalRuntime.WaitUntilReadyAsync(http, new("http://127.0.0.1/health"), () => !cancelled, new Observer<string>(messages.Add), new CancellationToken(cancelled), TimeSpan.Zero).GetAwaiter().GetResult(); throw new Exception("Expected startup failure"); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { Check(messages.All(m => !m.Contains("ready"))); }
            }
        });
        test("Model status preserves request payloads and reports scoped files after readiness", () => {
            var updates = new List<ModelActivity>(); var bodies = new List<string>(); var ready = false;
            using var http = new HttpClient(new Handler(async (request, ct) => {
                Check(request.Method == HttpMethod.Post && ready); bodies.Add(await request.Content!.ReadAsStringAsync(ct)); return Result();
            }));
            var model = new ModelTransport(http, new(), _ => { Check(updates.Count == 0); ready = true; return Task.CompletedTask; }, new Observer<ModelActivity>(updates.Add));
            using (model.ForSource("episode-a.mkv")) {
                var answer = model.CompleteAsync("m", "http://127.0.0.1/v1/chat/completions", "private dialogue", new(), new JsonObject(), default, "Checking episode match").GetAwaiter().GetResult();
                Check(answer.Bool("ok") && updates.Count == 2 && updates.All(x => x.Source == "episode-a.mkv" && x.Purpose == "Checking episode match"));
                Check(updates[0].State == "Waiting for model response" && updates[^1].State == "Model response received");
            }
            updates.Clear();
            model.CompleteAsync("m", "http://127.0.0.1/v1/chat/completions", "private dialogue", new(), new JsonObject(), default).GetAwaiter().GetResult();
            Check(updates.All(x => x.Source == "" && !x.Detail.Contains("private")) && bodies[0] == bodies[1]);
            Check(model.Usage is { Requests: 2, InputTokens: 24, OutputTokens: 6 });
        });
        test("Request failures and cancellation retain their outcomes without leaking details to status", () => {
            foreach (var mode in new[] { "failed", "timeout", "cancelled" }) {
                var updates = new List<ModelActivity>(); using var cancel = new CancellationTokenSource();
                using var http = new HttpClient(new Handler((_, ct) => {
                    if (mode == "cancelled") { cancel.Cancel(); throw new OperationCanceledException(ct); }
                    if (mode == "timeout") throw new TaskCanceledException("private details", new TimeoutException());
                    throw new IOException("private details");
                }));
                var model = new ModelTransport(http, new(), activity: new Observer<ModelActivity>(updates.Add));
                try { model.CompleteAsync("m", "http://127.0.0.1/v1/chat/completions", "private prompt", new(), new JsonObject(), cancel.Token).GetAwaiter().GetResult(); throw new Exception("Expected failure"); }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) {
                    Check(updates.Count == 2 && updates[^1].State == "Model request " + (mode == "timeout" ? "timed out" : mode));
                    Check(updates.All(u => !JsonSerializer.Serialize(u).Contains("private")));
                }
            }
        });
        test("Managed progress reports input and generation counters and stops before completion", () => {
            async Task Run() {
                var updates = new ConcurrentQueue<ModelActivity>(); var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var polls = 0;
                using var http = new HttpClient(new Handler((request, _) => {
                    Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == "http://127.0.0.1:1234/slots" && request.Headers.Authorization == null);
                    var generated = Interlocked.Increment(ref polls) > 1 ? 8 : 0;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""[{"is_processing":true,"n_prompt_tokens":100,"n_prompt_tokens_processed":50,"private_prompt":"never display","next_token":[{"n_decoded":{{generated}}}]}]""") });
                }));
                await using var status = new ModelRequestActivity(new Observer<ModelActivity>(u => { updates.Enqueue(u); if (u.State.Contains("Generating")) seen.TrySetResult(); }), 2, "Verifying evidence", "episode.mkv", "managed", new("http://127.0.0.1:1234/v1/chat/completions"), default, http, TimeSpan.FromMilliseconds(10));
                await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)); await status.FinishAsync("Model response received");
                var count = updates.Count; await Task.Delay(50); Check(updates.Count == count);
                Check(updates.Any(x => x.State.Contains("50 tokens processed")) && updates.Any(x => x.State.Contains("8 tokens generated")) && updates.Last().State == "Model response received");
                Check(updates.All(x => !JsonSerializer.Serialize(x).Contains("never display")));
            }
            Run().GetAwaiter().GetResult();
        });
        test("Unavailable telemetry and external servers use elapsed status without failing requests", () => {
            async Task Run(string provider) {
                var updates = new ConcurrentQueue<ModelActivity>(); var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
                using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }));
                await using var status = new ModelRequestActivity(new Observer<ModelActivity>(u => { updates.Enqueue(u); if (u.IsHeartbeat) pulse.TrySetResult(); }), 1, "Finding candidates", "file.mkv", provider, new(provider == "managed" ? "http://127.0.0.1/v1/chat/completions" : "https://example.invalid/v1/chat/completions"), default, http, TimeSpan.FromMilliseconds(10));
                await pulse.Task.WaitAsync(TimeSpan.FromSeconds(5)); await status.FinishAsync("Model response received");
                Check((provider == "managed" ? calls > 0 : calls == 0) && updates.Any(x => x.IsHeartbeat && x.State == "Waiting for model response"));
            }
            Run("managed").GetAwaiter().GetResult(); Run("compatible").GetAwaiter().GetResult();
        });
        test("Malformed or idle slot counters never invent processing percentages", () => {
            foreach (var json in new[] { "[]", "[{},{}]", "[{\"is_processing\":false}]", "[{\"is_processing\":true,\"n_prompt_tokens_processed\":-1}]", "[{\"is_processing\":true,\"n_prompt_tokens_processed\":\"private\"}]" }) Check(ModelRequestActivity.SlotState(json) == null);
            Check(ModelRequestActivity.SlotState("[{\"is_processing\":true,\"n_prompt_tokens\":10,\"n_prompt_tokens_processed\":10}]") == "Reading model input · 10 tokens processed");
            Check(ModelRequestActivity.SlotState("[{\"is_processing\":true,\"n_prompt_tokens\":200,\"n_prompt_tokens_processed\":10}]") == "Reading model input · 10 tokens processed");
        });
        test("A failing status observer cannot break a model response", () => {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Result())));
            var model = new ModelTransport(http, new(), activity: new Observer<ModelActivity>(_ => throw new IOException("UI observer unavailable")));
            Check(model.CompleteAsync("m", "http://127.0.0.1/v1/chat/completions", "p", new(), new JsonObject(), default).GetAwaiter().GetResult().Bool("ok"));
        });
    }
    sealed class Observer<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
}
