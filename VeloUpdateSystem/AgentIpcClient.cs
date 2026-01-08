using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VeloUpdateSystem;

public static class AgentIpcClient
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:51234/"),
        Timeout = TimeSpan.FromSeconds(3)
    };

    public static Task SendHeartbeatAsync(int pid, bool responsive, int idleMinutes, CancellationToken cancellationToken)
    {
        var payload = new { pid, responsive, idleMinutes };
        return Client.PostAsJsonAsync("heartbeat", payload, cancellationToken);
    }

    public static async Task<JsonElement?> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync("status", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }
}
