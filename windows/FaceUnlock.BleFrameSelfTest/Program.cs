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

// Cross-platform vector tests for Canonical formatting and RFC3279 DER ECDSA verification
var testSessionId = "u4xh-GumauT524IfxlALB6zR";
var testChallenge = "test-challenge-1234567890";
var testPcId = "pc-12345";
long testExpiresAt = 1771234567;
var expectedCanonical = "faceunlock-v1|u4xh-GumauT524IfxlALB6zR|test-challenge-1234567890|pc-12345|1771234567";
var actualCanonical = Protocol.Canonical(testSessionId, testChallenge, testPcId, testExpiresAt);
Require(actualCanonical == expectedCanonical, "Canonical string mismatch.");

// Test RFC3279 DER ECDSA round-trip and verification
using (var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
{
    var pubPem = ecdsa.ExportSubjectPublicKeyInfoPem();
    var sigDer = ecdsa.SignData(System.Text.Encoding.UTF8.GetBytes(actualCanonical), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
    var sigB64 = Convert.ToBase64String(sigDer);
    Require(KeyStore.VerifyPem(pubPem, actualCanonical, sigB64), "DER ECDSA verification failed on valid signature.");
    Require(!KeyStore.VerifyPem(pubPem, actualCanonical + "tampered", sigB64), "Tampered message was accepted.");
}

Console.WriteLine("Crypto & Canonical cross-platform self-test PASS.");
