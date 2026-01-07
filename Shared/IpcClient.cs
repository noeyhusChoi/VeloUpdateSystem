using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VeloUpdateSystem.Shared;

public static class IpcClient
{
    public static async Task SendAsync(string pipeName, IpcEnvelope envelope, int timeoutMs, CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        using var writer = new StreamWriter(client, Encoding.UTF8, 4096, true) { AutoFlush = true };
        var json = JsonSerializer.Serialize(envelope, IpcEnvelope.JsonOptions);
        await writer.WriteLineAsync(json).ConfigureAwait(false);
    }
}
