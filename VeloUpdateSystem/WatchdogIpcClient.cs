using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VeloUpdateSystem;

public sealed class WatchdogIpcClient
{
    private readonly HttpClient _client;

    public WatchdogIpcClient(Uri baseUri)
    {
        _client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public Task SendHeartbeatAsync(int pid, bool responsive, int idleSeconds, CancellationToken cancellationToken)
    {
        var payload = new { pid, responsive, idleSeconds };
        return _client.PostAsJsonAsync("heartbeat", payload, cancellationToken);
    }

    public async Task<JsonElement?> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("status", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    public Task SetUpdateModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        var path = enabled ? "update/start" : "update/end";
        return _client.PostAsync(path, content: null, cancellationToken);
    }
}
