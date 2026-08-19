using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FaceUnlock.Core;

public enum BleFrameKind : byte
{
    Request = 1,
    Response = 2
}

public enum BleFrameDecodeStatus
{
    Legacy,
    Frame,
    Invalid
}

public enum BleAssemblyStatus
{
    Waiting,
    Complete,
    Invalid
}

public readonly record struct BleDecodedFrame(
    BleFrameKind Kind,
    ushort MessageId,
    ushort ChunkIndex,
    ushort ChunkCount,
    byte[] Payload
);

/// <summary>
/// MTU-safe application framing for the FaceUnlock BLE transport.
///
/// Frame layout, network byte order:
///   0..1  magic "FU"
///   2     high nibble = version (1), low nibble = kind (1=request, 2=response)
///   3..4  message id UInt16
///   5..6  chunk index UInt16 (zero based)
///   7..8  chunk count UInt16
///   9..   payload
///
/// The default frame size is 20 bytes so it works at the minimum ATT MTU.
/// Receivers accept larger frames, allowing iOS to use the negotiated
/// notification size for responses.
/// </summary>
public static class BleFrameCodec
{
    public const int HeaderSize = 9;
    public const int MinimumFrameSize = 20;
    public const int MaximumMessageBytes = 16 * 1024;
    public static readonly TimeSpan AssemblyTimeout = TimeSpan.FromSeconds(15);

    private const byte Magic0 = 0x46; // F
    private const byte Magic1 = 0x55; // U
    private const byte Version = 1;

    public static IReadOnlyList<byte[]> Encode(
        ReadOnlySpan<byte> payload,
        BleFrameKind kind,
        int maximumFrameBytes = MinimumFrameSize,
        ushort? messageId = null)
    {
        if (payload.Length > MaximumMessageBytes)
            throw new InvalidOperationException($"BLE message exceeds {MaximumMessageBytes} bytes.");

        var frameBytes = Math.Max(MinimumFrameSize, maximumFrameBytes);
        var payloadPerFrame = frameBytes - HeaderSize;
        if (payloadPerFrame <= 0)
            throw new InvalidOperationException("Invalid BLE frame size.");

        var count = Math.Max(1, (payload.Length + payloadPerFrame - 1) / payloadPerFrame);
        if (count > ushort.MaxValue)
            throw new InvalidOperationException("Too many BLE chunks.");

        var id = messageId ?? (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue + 1);
        var frames = new List<byte[]>(count);

        for (var index = 0; index < count; index++)
        {
            var start = index * payloadPerFrame;
            var length = Math.Min(payloadPerFrame, Math.Max(0, payload.Length - start));
            var frame = new byte[HeaderSize + length];

            frame[0] = Magic0;
            frame[1] = Magic1;
            frame[2] = (byte)((Version << 4) | ((byte)kind & 0x0F));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3, 2), id);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(5, 2), checked((ushort)index));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(7, 2), checked((ushort)count));

            if (length > 0)
                payload.Slice(start, length).CopyTo(frame.AsSpan(HeaderSize));

            frames.Add(frame);
        }

        return frames;
    }

    public static BleFrameDecodeStatus Decode(
        ReadOnlySpan<byte> data,
        out BleDecodedFrame frame,
        out byte[]? legacy,
        out string? error)
    {
        frame = default;
        legacy = null;
        error = null;

        if (data.Length < 2 || data[0] != Magic0 || data[1] != Magic1)
        {
            legacy = data.ToArray();
            return BleFrameDecodeStatus.Legacy;
        }

        if (data.Length < HeaderSize)
        {
            error = "Truncated BLE frame.";
            return BleFrameDecodeStatus.Invalid;
        }

        var versionAndKind = data[2];
        var version = (byte)(versionAndKind >> 4);
        var kindRaw = (byte)(versionAndKind & 0x0F);

        if (version != Version)
        {
            error = $"Unsupported BLE frame version {version}.";
            return BleFrameDecodeStatus.Invalid;
        }

        if (!Enum.IsDefined(typeof(BleFrameKind), kindRaw))
        {
            error = "Unknown BLE frame kind.";
            return BleFrameDecodeStatus.Invalid;
        }

        var messageId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3, 2));
        var chunkIndex = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(5, 2));
        var chunkCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(7, 2));

        if (messageId == 0 || chunkCount == 0 || chunkIndex >= chunkCount)
        {
            error = "Invalid BLE frame indexes.";
            return BleFrameDecodeStatus.Invalid;
        }

        frame = new BleDecodedFrame(
            (BleFrameKind)kindRaw,
            messageId,
            chunkIndex,
            chunkCount,
            data.Slice(HeaderSize).ToArray()
        );
        return BleFrameDecodeStatus.Frame;
    }
}

public sealed class BleFrameAssembler
{
    private sealed class State
    {
        public required BleFrameKind Kind { get; init; }
        public required ushort ChunkCount { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public Dictionary<ushort, byte[]> Chunks { get; } = new();
        public int TotalBytes { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<ushort, State> _states = new();

    public BleAssemblyStatus Ingest(
        ReadOnlySpan<byte> data,
        BleFrameKind expectedKind,
        out byte[]? complete,
        out bool framed,
        out string? error)
    {
        lock (_sync)
        {
            CleanupExpired();

            complete = null;
            framed = false;
            error = null;

            var decode = BleFrameCodec.Decode(data, out var frame, out var legacy, out error);
            if (decode == BleFrameDecodeStatus.Invalid)
                return BleAssemblyStatus.Invalid;

            if (decode == BleFrameDecodeStatus.Legacy)
            {
                if (legacy is null || legacy.Length > BleFrameCodec.MaximumMessageBytes)
                {
                    error = "Legacy BLE message is too large.";
                    return BleAssemblyStatus.Invalid;
                }

                complete = legacy;
                framed = false;
                return BleAssemblyStatus.Complete;
            }

            framed = true;
            if (frame.Kind != expectedKind)
            {
                error = "Unexpected BLE frame kind.";
                return BleAssemblyStatus.Invalid;
            }

            if (!_states.TryGetValue(frame.MessageId, out var state))
            {
                state = new State
                {
                    Kind = frame.Kind,
                    ChunkCount = frame.ChunkCount,
                    CreatedUtc = DateTime.UtcNow
                };
                _states[frame.MessageId] = state;
            }
            else if (state.Kind != frame.Kind || state.ChunkCount != frame.ChunkCount)
            {
                _states.Remove(frame.MessageId);
                error = "BLE frame metadata changed mid-message.";
                return BleAssemblyStatus.Invalid;
            }

            if (!state.Chunks.ContainsKey(frame.ChunkIndex))
            {
                state.Chunks[frame.ChunkIndex] = frame.Payload;
                state.TotalBytes += frame.Payload.Length;
            }

            if (state.TotalBytes > BleFrameCodec.MaximumMessageBytes)
            {
                _states.Remove(frame.MessageId);
                error = "BLE message exceeds maximum size.";
                return BleAssemblyStatus.Invalid;
            }

            if (state.Chunks.Count != state.ChunkCount)
                return BleAssemblyStatus.Waiting;

            using var ms = new MemoryStream(state.TotalBytes);
            for (ushort i = 0; i < state.ChunkCount; i++)
            {
                if (!state.Chunks.TryGetValue(i, out var part))
                    return BleAssemblyStatus.Waiting;
                ms.Write(part, 0, part.Length);
            }

            complete = ms.ToArray();
            _states.Remove(frame.MessageId);
            return BleAssemblyStatus.Complete;
        }
    }

    public void Reset()
    {
        lock (_sync)
            _states.Clear();
    }

    private void CleanupExpired()
    {
        var cutoff = DateTime.UtcNow - BleFrameCodec.AssemblyTimeout;
        foreach (var id in _states
                     .Where(kvp => kvp.Value.CreatedUtc < cutoff)
                     .Select(kvp => kvp.Key)
                     .ToArray())
        {
            _states.Remove(id);
        }
    }
}
