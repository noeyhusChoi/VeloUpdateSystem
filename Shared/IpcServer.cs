using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VeloUpdateSystem.Shared;

public sealed class IpcServer
{
    private readonly string _pipeName;
    private readonly Func<IpcEnvelope, Task<IpcEnvelope?>> _handler;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();

    public IpcServer(string pipeName, Func<IpcEnvelope, Task<IpcEnvelope?>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public ClientConnection[] GetClients()
    {
        return _clients.Values.ToArray();
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => AcceptLoopAsync(cancellationToken), cancellationToken);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connection = new ClientConnection(server, RemoveClient);
                _clients[connection.Id] = connection;
                _ = connection.RunAsync(_handler, cancellationToken);
            }
            catch
            {
                server.Dispose();
            }
        }
    }

    private void RemoveClient(Guid id)
    {
        _clients.TryRemove(id, out _);
    }

    public sealed class ClientConnection
    {
        private readonly NamedPipeServerStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly Action<Guid> _onClose;

        public ClientConnection(NamedPipeServerStream stream, Action<Guid> onClose)
        {
            _stream = stream;
            _reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            _writer = new StreamWriter(stream, Encoding.UTF8, 4096, true) { AutoFlush = true };
            _onClose = onClose;
            Id = Guid.NewGuid();
        }

        public Guid Id { get; }

        public async Task RunAsync(Func<IpcEnvelope, Task<IpcEnvelope?>> handler, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _stream.IsConnected)
                {
                    var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    var envelope = JsonSerializer.Deserialize<IpcEnvelope>(line, IpcEnvelope.JsonOptions);
                    if (envelope is null)
                    {
                        continue;
                    }

                    var response = await handler(envelope).ConfigureAwait(false);
                    if (response is not null)
                    {
                        await SendAsync(response).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _stream.Dispose();
                _onClose(Id);
            }
        }

        public Task SendAsync(IpcEnvelope envelope)
        {
            var json = JsonSerializer.Serialize(envelope, IpcEnvelope.JsonOptions);
            return _writer.WriteLineAsync(json);
        }
    }
}
