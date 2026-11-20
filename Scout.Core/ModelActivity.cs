using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scout.Core;

public sealed record ModelActivity(int Request, string Purpose, string Source, string State, TimeSpan Elapsed, bool IsHeartbeat = false)
{
    public string Heading => $"AI request {Request} · {Purpose}";
    public string Detail => $"{State} · {Elapsed.TotalSeconds:0}s elapsed";
}

// Status never changes the model request, its timeout, or its result. Only the
// dedicated Scout-managed server is polled; no prompts, responses or keys are logged.
internal sealed class ModelRequestActivity : IAsyncDisposable
{
    readonly IProgress<ModelActivity>? progress;
    readonly int request;
    readonly string purpose, source;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly CancellationTokenSource stop;
    readonly Task monitor;
    readonly HttpClient? telemetry;
    readonly bool ownsClient;
    bool finished;

    internal ModelRequestActivity(IProgress<ModelActivity>? progress, int request, string purpose, string source,
        string provider, Uri endpoint, CancellationToken ct, HttpClient? telemetry = null, TimeSpan? interval = null)
    {
        this.progress = progress; this.request = request; this.purpose = purpose; this.source = source;
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (progress != null && provider == "managed") {
            DialogueMatcher.LocalEndpoint(endpoint.AbsoluteUri);
            this.telemetry = telemetry ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
            ownsClient = telemetry == null;
        }
        Report("Waiting for model response");
        monitor = progress == null ? Task.CompletedTask : MonitorAsync(new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/slots"), interval ?? TimeSpan.FromSeconds(5));
    }

    void Report(string state, bool heartbeat = false)
    {
        // An observer failure must not interrupt inference or replace its outcome.
        try { progress?.Report(new(request, purpose, source, state, clock.Elapsed, heartbeat)); } catch (Exception) { }
    }

    async Task MonitorAsync(Uri slots, TimeSpan interval)
    {
        try {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false)) {
                var state = "Waiting for model response";
                if (telemetry != null) {
                    try {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(2));
                        using var response = await telemetry.GetAsync(slots, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode) {
                            await response.Content.LoadIntoBufferAsync(65536, deadline.Token).ConfigureAwait(false);
                            state = SlotState(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)) ?? state;
                        }
                    } catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException) { }
                }
                if (!stop.IsCancellationRequested) Report(state, true);
            }
        } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    internal static string? SlotState(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray { Count: 1 } slots || slots[0] is not JsonObject slot ||
            slot["is_processing"] is not JsonValue processing || !processing.TryGetValue<bool>(out var active) || !active) return null;
        static long Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var n) && n >= 0 ? n : -1;
        var generated = slot["next_token"] is JsonArray { Count: > 0 } next ? Number(next[0]?["n_decoded"]) : -1;
        if (generated > 0) return $"Generating response · {generated:N0} tokens generated";
        // b11908 exposes its growing prompt buffer as n_prompt_tokens, not a fixed
        // request total. Do not turn it into a misleading fraction or percentage.
        // https://github.com/ggml-org/llama.cpp/blob/b11908/tools/server/server-context.cpp#L747
        var read = Number(slot["n_prompt_tokens_processed"]);
        return read < 0 ? null : $"Reading model input · {read:N0} tokens processed";
    }

    internal async Task FinishAsync(string state)
    {
        if (finished) return;
        finished = true; stop.Cancel(); await monitor.ConfigureAwait(false); Report(state);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); await monitor.ConfigureAwait(false); stop.Dispose(); if (ownsClient) telemetry?.Dispose();
    }
}
