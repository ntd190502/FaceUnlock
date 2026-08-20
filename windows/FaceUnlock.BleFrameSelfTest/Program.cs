using System.Text.Json;
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

// Test LocalAuth IPC Model Serialization / Deserialization
var req = new LocalAuthRequest(1, "request_unlock", "req-12345-abc", "unlock", "testuser", "S-1-5-21-0000", "TEST\\testuser", 1);
var reqJson = JsonSerializer.Serialize(req, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var reqParsed = JsonSerializer.Deserialize<LocalAuthRequest>(reqJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Require(reqParsed != null && reqParsed.command == "request_unlock" && reqParsed.request_id == "req-12345-abc", "LocalAuthRequest JSON serialization failure.");

var pingReq = new LocalAuthRequest(1, "ping", "req-ping-123");
var pingReqJson = JsonSerializer.Serialize(pingReq, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var pingReqParsed = JsonSerializer.Deserialize<LocalAuthRequest>(pingReqJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Require(pingReqParsed != null && pingReqParsed.command == "ping", "ping request serialization failure.");

var pingResp = new LocalAuthResponse(1, "req-ping-123", LocalAuthStatus.Ok, "FaceUnlock Service is healthy", null, "1.1.0");
var pingRespJson = JsonSerializer.Serialize(pingResp, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var pingRespParsed = JsonSerializer.Deserialize<LocalAuthResponse>(pingRespJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Require(pingRespParsed != null && pingRespParsed.status == LocalAuthStatus.Ok && pingRespParsed.service_version == "1.1.0", "ping response serialization failure.");

var reserveReq = new LocalAuthRequest(1, "reserve_grant", "req-12345-abc");
var reserveReqJson = JsonSerializer.Serialize(reserveReq, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var reserveReqParsed = JsonSerializer.Deserialize<LocalAuthRequest>(reserveReqJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Require(reserveReqParsed != null && reserveReqParsed.command == "reserve_grant", "reserve_grant request serialization failure.");

var releaseReq = new LocalAuthRequest(1, "release_grant", "req-12345-abc");
var releaseReqJson = JsonSerializer.Serialize(releaseReq, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var releaseReqParsed = JsonSerializer.Deserialize<LocalAuthRequest>(releaseReqJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Require(releaseReqParsed != null && releaseReqParsed.command == "release_grant", "release_grant request serialization failure.");

var cancelReq = new LocalAuthRequest(1, "cancel_request", "req-12345-abc");
var cancelReqJson = JsonSerializer.Serialize(cancelReq, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var cancelReqParsed = JsonSerializer.Deserialize<LocalAuthRequest>(cancelReqJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Require(cancelReqParsed != null && cancelReqParsed.command == "cancel_request", "cancel_request serialization failure.");

var resp = new LocalAuthResponse(1, "req-12345-abc", LocalAuthStatus.Approved, "Face ID approved", 1771234567);
var respJson = JsonSerializer.Serialize(resp, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var respParsed = JsonSerializer.Deserialize<LocalAuthResponse>(respJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Require(respParsed != null && respParsed.status == LocalAuthStatus.Approved && respParsed.expires_at == 1771234567, "LocalAuthResponse JSON serialization failure.");

var consumeResp = new LocalAuthResponse(1, "req-12345-abc", LocalAuthStatus.Consumed, "grant_consumed", 1771234567);
var consumeRespJson = JsonSerializer.Serialize(consumeResp, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var consumeRespParsed = JsonSerializer.Deserialize<LocalAuthResponse>(consumeRespJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Require(consumeRespParsed != null && consumeRespParsed.status == LocalAuthStatus.Consumed && consumeRespParsed.message == "grant_consumed", "consume_grant response serialization failure.");

Console.WriteLine("LocalAuth IPC Model self-test PASS.");
