using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SpotiFetch;

/// <summary>Publishes the local player state to the running Discord desktop client.</summary>
public sealed class DiscordPresenceService : IAsyncDisposable
{
    private const int RpcVersion = 1;
    private const int MaximumFrameLength = 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private string? _applicationId;

    public async Task UpdateAsync(
        string applicationId,
        string title,
        string artist,
        string? artworkUrl,
        TimeSpan position,
        TimeSpan duration,
        bool isPlaying,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidApplicationId(applicationId))
            throw new ArgumentException("Informe um Application ID numérico válido do Discord.", nameof(applicationId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(applicationId, cancellationToken);

            object? timestamps = null;
            if (isPlaying && duration > TimeSpan.Zero)
            {
                var start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Math.Max(0, (long)position.TotalSeconds);
                timestamps = new { start, end = start + (long)duration.TotalSeconds };
            }

            object? assets = Uri.TryCreate(artworkUrl, UriKind.Absolute, out var imageUri) && imageUri.Scheme == Uri.UriSchemeHttps
                ? new { large_image = LimitText(imageUri.AbsoluteUri, 300), large_text = LimitText(title, 128) }
                : null;

            var activity = new
            {
                type = 2, // Listening
                details = LimitText(title, 128),
                state = LimitText(string.IsNullOrWhiteSpace(artist) ? "Artista desconhecido" : artist, 128),
                timestamps,
                assets,
                instance = false
            };

            await SendCommandAsync("SET_ACTIVITY", new { pid = Environment.ProcessId, activity }, cancellationToken);
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_pipe?.IsConnected == true)
                await SendCommandAsync("SET_ACTIVITY", new { pid = Environment.ProcessId, activity = (object?)null }, cancellationToken);
        }
        catch { /* Discord may already have closed; clearing presence is best effort. */ }
        finally
        {
            await DisconnectAsync();
            _gate.Release();
        }
    }

    public static bool IsValidApplicationId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 17 and <= 20 && value.All(char.IsAsciiDigit);

    private async Task EnsureConnectedAsync(string applicationId, CancellationToken cancellationToken)
    {
        if (_pipe?.IsConnected == true && string.Equals(_applicationId, applicationId, StringComparison.Ordinal)) return;
        await DisconnectAsync();

        for (var index = 0; index < 10; index++)
        {
            var candidate = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await candidate.ConnectAsync(350, cancellationToken);
                _pipe = candidate;
                _applicationId = applicationId;
                await WriteFrameAsync(0, new { v = RpcVersion, client_id = applicationId }, cancellationToken);
                var ready = await ReadFrameAsync(cancellationToken);
                if (ready.Opcode != 1 || !ready.Payload.Contains("\"READY\"", StringComparison.Ordinal))
                    throw new IOException("O Discord não confirmou a conexão RPC.");
                return;
            }
            catch
            {
                candidate.Dispose();
                if (ReferenceEquals(_pipe, candidate))
                {
                    _pipe = null;
                    _applicationId = null;
                }
            }
        }

        throw new IOException("Não encontrei o Discord aberto. Abra o aplicativo Discord para publicar a atividade.");
    }

    private async Task SendCommandAsync(string command, object arguments, CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        await WriteFrameAsync(1, new { cmd = command, args = arguments, nonce }, cancellationToken);
        var response = await ReadFrameAsync(cancellationToken);
        if (response.Opcode != 1) throw new IOException("O Discord encerrou a conexão RPC.");

        using var document = JsonDocument.Parse(response.Payload);
        if (document.RootElement.TryGetProperty("evt", out var eventType) && eventType.GetString() == "ERROR")
        {
            var message = document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var text)
                ? text.GetString() : null;
            throw new IOException(string.IsNullOrWhiteSpace(message) ? "O Discord recusou a atividade." : message);
        }
    }

    private async Task WriteFrameAsync(int opcode, object payload, CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new IOException("A conexão RPC com o Discord não está ativa.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), bytes.Length);
        await pipe.WriteAsync(header, cancellationToken);
        await pipe.WriteAsync(bytes, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }

    private async Task<(int Opcode, string Payload)> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new IOException("A conexão RPC com o Discord não está ativa.");
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, cancellationToken);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 0 or > MaximumFrameLength) throw new IOException("O Discord enviou um quadro RPC inválido.");
        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, cancellationToken);
        return (opcode, Encoding.UTF8.GetString(payload));
    }

    private async Task DisconnectAsync()
    {
        if (_pipe is not null)
        {
            try { await _pipe.DisposeAsync(); }
            catch { }
        }
        _pipe = null;
        _applicationId = null;
    }

    private static string LimitText(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    public async ValueTask DisposeAsync()
    {
        await ClearAsync();
        _gate.Dispose();
    }
}
