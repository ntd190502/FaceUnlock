using FaceUnlock.Core;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var payload = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();
var frames = BleFrameCodec.Encode(
    payload,
    BleFrameKind.Request,
    BleFrameCodec.MinimumFrameSize,
    messageId: 0x1234
);

Require(frames.Count > 1, "Expected a multi-frame payload.");
Require(frames.All(f => f.Length <= BleFrameCodec.MinimumFrameSize), "Frame exceeded 20 bytes.");

var assembler = new BleFrameAssembler();
byte[]? reassembled = null;
foreach (var frame in frames.Reverse())
{
    var status = assembler.Ingest(
        frame,
        BleFrameKind.Request,
        out var complete,
        out var framed,
        out var error
    );
    if (status == BleAssemblyStatus.Invalid)
        throw new InvalidOperationException(error ?? "Unexpected invalid frame.");
    if (status == BleAssemblyStatus.Complete)
    {
        Require(framed, "Framed message was marked legacy.");
        reassembled = complete;
    }
}
Require(reassembled is not null && reassembled.SequenceEqual(payload), "Out-of-order reassembly mismatch.");

var duplicateAssembler = new BleFrameAssembler();
_ = duplicateAssembler.Ingest(frames[0], BleFrameKind.Request, out _, out _, out _);
_ = duplicateAssembler.Ingest(frames[0], BleFrameKind.Request, out _, out _, out _);
byte[]? duplicateResult = null;
foreach (var frame in frames.Skip(1))
{
    if (duplicateAssembler.Ingest(frame, BleFrameKind.Request, out var complete, out _, out var error)
        == BleAssemblyStatus.Invalid)
        throw new InvalidOperationException(error ?? "Duplicate test failed.");
    if (complete is not null) duplicateResult = complete;
}
Require(duplicateResult is not null && duplicateResult.SequenceEqual(payload), "Duplicate-frame handling mismatch.");

var legacy = System.Text.Encoding.UTF8.GetBytes("legacy-faceunlock");
var legacyStatus = new BleFrameAssembler().Ingest(
    legacy,
    BleFrameKind.Request,
    out var legacyComplete,
    out var legacyFramed,
    out _
);
Require(legacyStatus == BleAssemblyStatus.Complete && !legacyFramed, "Legacy compatibility failed.");
Require(legacyComplete is not null && legacyComplete.SequenceEqual(legacy), "Legacy payload mismatch.");

var oversizeRejected = false;
try
{
    _ = BleFrameCodec.Encode(
        new byte[BleFrameCodec.MaximumMessageBytes + 1],
        BleFrameKind.Request
    );
}
catch (InvalidOperationException)
{
    oversizeRejected = true;
}
Require(oversizeRejected, "Oversized message was accepted.");

Console.WriteLine($"BleFrameCodec C# self-test PASS: {frames.Count} frames");
