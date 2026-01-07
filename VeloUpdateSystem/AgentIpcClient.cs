using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VeloUpdateSystem.Shared;

namespace VeloUpdateSystem;

public static class AgentIpcClient
{
    private const string AgentPipeName = "Moneybox.Agent";

    public static Task SendHeartbeatAsync(int pid, bool responsive, int idleMinutes, CancellationToken cancellationToken)
    {
        var payload = new { pid, responsive, idleMinutes };
        var message = IpcEnvelope.Create(IpcMessageTypes.Heartbeat, "App", "Agent", payload);
        return IpcClient.SendAsync(AgentPipeName, message, timeoutMs: 500, cancellationToken);
    }

    public static async Task<IpcEnvelope?> GetStatusAsync(CancellationToken cancellationToken)
    {
        var request = IpcEnvelope.Create(IpcMessageTypes.Status, "App", "Agent", new { want = "status" });
        using var client = new NamedPipeClientStream(".", AgentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(1000);

        await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(client, Encoding.UTF8, 4096, true) { AutoFlush = true };

        var json = JsonSerializer.Serialize(request, IpcEnvelope.JsonOptions);
        await writer.WriteLineAsync(json).ConfigureAwait(false);

        var responseLine = await reader.ReadLineAsync().ConfigureAwait(false);
        if (responseLine is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<IpcEnvelope>(responseLine, IpcEnvelope.JsonOptions);
    }
}
