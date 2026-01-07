using System;
using System.Text.Json;

namespace VeloUpdateSystem.Shared;

public sealed record IpcEnvelope(
    string Id,
    string Type,
    string Source,
    string Target,
    string Timestamp,
    string? CorrelationId,
    JsonElement Payload
)
{
    public static IpcEnvelope Create(string type, string source, string target, object payload, string? correlationId = null)
    {
        return new IpcEnvelope(
            Guid.NewGuid().ToString(),
            type,
            source,
            target,
            DateTimeOffset.UtcNow.ToString("O"),
            correlationId,
            JsonSerializer.SerializeToElement(payload, JsonOptions)
        );
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
