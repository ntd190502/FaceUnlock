using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FaceUnlock.Core;
using FaceUnlock.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace FaceUnlock.IpcIntegrationTests;

public sealed class TestShellClientAuthorizer : IShellClientAuthorizer
{
    public bool IsTrustedShellClient(int clientProcessId, LocalAuthRequest request, out string? errorMessage)
    {
        errorMessage = null;
        return true;
    }
}

public sealed class TestNoOpWatchdog : IShellGateWatchdog
{
    public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TestBluetoothRadioManager : IBluetoothRadioManager
{
    public BluetoothState State { get; private set; } = BluetoothState.Disabled;
    public int DisableCalls { get; private set; }

    public Task<BluetoothRadioStatus> GetStateAsync(CancellationToken ct = default) =>
        Task.FromResult(new BluetoothRadioStatus(State));

    public Task<BluetoothRadioStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        State = enabled ? BluetoothState.Enabled : BluetoothState.Disabled;
        if (!enabled) DisableCalls++;
        return Task.FromResult(new BluetoothRadioStatus(State, enabled));
    }

    public async Task<BluetoothRadioStatus> EnsureEnabledAsync(CancellationToken ct = default) =>
        State == BluetoothState.Enabled ? new BluetoothRadioStatus(State) : await SetEnabledAsync(true, ct);
}

public class Program
{
    private const string PipeName = "FaceUnlock.Auth.Test";

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  FaceUnlock IPC Integration Tests (C# & Named Pipes)");
        Console.WriteLine("============================================================");

        var cts = new CancellationTokenSource();
        var gateAuthority = new SessionGateAuthority();
        var testRadio = new TestBluetoothRadioManager();
        var testBluetoothLeases = new BluetoothLeaseManager(testRadio);
        var worker = new UnlockWorker(
            NullLogger<UnlockWorker>.Instance,
            PipeName,
            gateAuthority,
            new TestShellClientAuthorizer(),
            new TestNoOpWatchdog(),
            testRadio,
            testBluetoothLeases);
        var clientProcessId = Environment.ProcessId;

        // Start UnlockWorker as background task
        var serviceTask = worker.StartAsync(cts.Token);

        // Give the service a brief moment to initialize and open the named pipe listener
        await Task.Delay(500);

        int passed = 0;
        int failed = 0;

        void Check(bool cond, string name, string reason = "")
        {
            if (cond)
            {
                passed++;
                Console.WriteLine($"  [PASS] {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  [FAIL] {name} - {reason}");
            }
        }

        void RegisterShellRequest(string requestId, string sid, int sessionId)
        {
            gateAuthority.ObserveLockedSession(sessionId, sid);
            gateAuthority.TryRegisterShellProcess(sessionId, sid, clientProcessId, out _);
            gateAuthority.TryBeginShellRequest(sessionId, sid, clientProcessId, requestId, out _);
        }

        async Task<LocalAuthResponse?> SendIpcCommandAsync(LocalAuthRequest req, int timeoutMs = 3000)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeoutMs);

                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, true) { AutoFlush = true };

                var json = JsonSerializer.Serialize(req);
                await writer.WriteLineAsync(json);

                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) return null;

                return JsonSerializer.Deserialize<LocalAuthResponse>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (Exception ex)
            {
                return new LocalAuthResponse(1, req.request_id, LocalAuthStatus.Error, ex.Message);
            }
        }

        try
        {
            // TEST A: Ping command
            Console.WriteLine("\n[Test A] ping command");
            var pingResp = await SendIpcCommandAsync(new LocalAuthRequest(1, "ping", Guid.NewGuid().ToString("N")));
            Check(pingResp != null && pingResp.status == LocalAuthStatus.Ok, "TEST A: Ping returns OK status");

            // TEST B: request_unlock returns immediate ACK without closed-pipe exception
            Console.WriteLine("\n[Test B] request_unlock immediate ACK");
            var reqIdB = Guid.NewGuid().ToString("N");
            var reqUnlockResp = await SendIpcCommandAsync(new LocalAuthRequest(1, "request_unlock", reqIdB, "logon", "S-1-5-21-0000", "S-1-5-21-0000", "TEST\\User"));
            Check(reqUnlockResp != null && (reqUnlockResp.status == LocalAuthStatus.Pending || reqUnlockResp.status == LocalAuthStatus.NotPaired),
                "TEST B: request_unlock returns ACK (pending/not_paired)", reqUnlockResp?.status ?? "null");

            // TEST C: grant_status pending/not_paired/active
            Console.WriteLine("\n[Test C] grant_status command");
            var statusResp = await SendIpcCommandAsync(new LocalAuthRequest(1, "grant_status", reqIdB));
            Check(statusResp != null && (statusResp.status == LocalAuthStatus.Pending || statusResp.status == LocalAuthStatus.NotPaired || statusResp.status == LocalAuthStatus.NotFound),
                "TEST C: grant_status returned valid response", statusResp?.status ?? "null");

            // TEST D: client disconnect immediately after ACK (no server exception)
            Console.WriteLine("\n[Test D] client disconnect immediately after reading response");
            var reqIdD = Guid.NewGuid().ToString("N");
            var respD = await SendIpcCommandAsync(new LocalAuthRequest(1, "request_unlock", reqIdD, "unlock", "S-1-5-21-0000", "S-1-5-21-0000", "TEST\\User"));
            Check(respD != null, "TEST D: client disconnect handled cleanly");

            // TEST E: 100 sequential requests (0 closed-pipe exceptions)
            Console.WriteLine("\n[Test E] 100 sequential requests");
            bool all100Ok = true;
            for (int i = 0; i < 100; i++)
            {
                var reqIdE = Guid.NewGuid().ToString("N");
                var r = await SendIpcCommandAsync(new LocalAuthRequest(1, (i % 2 == 0) ? "ping" : "grant_status", reqIdE));
                if (r == null || r.status == LocalAuthStatus.Error)
                {
                    all100Ok = false;
                    break;
                }
            }
            Check(all100Ok, "TEST E: 100 sequential requests executed with 0 closed-pipe errors");

            // TEST F: cancel_request
            Console.WriteLine("\n[Test F] cancel_request");
            var reqIdF = Guid.NewGuid().ToString("N");
            var cancelResp = await SendIpcCommandAsync(new LocalAuthRequest(1, "cancel_request", reqIdF));
            Check(cancelResp != null && cancelResp.status == LocalAuthStatus.Cancelled, "TEST F: cancel_request returns Cancelled");

            // TEST G: malformed JSON handling
            Console.WriteLine("\n[Test G] malformed JSON");
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(3000);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("{invalid-json-payload");
                var line = await reader.ReadLineAsync();
                var malformedResp = !string.IsNullOrWhiteSpace(line)
                    ? JsonSerializer.Deserialize<LocalAuthResponse>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    : null;
                Check(malformedResp != null && malformedResp.status == LocalAuthStatus.Error, "TEST G: malformed JSON returns Error response cleanly without service crash");
            }

            // TEST H: issue_lsa_ticket on invalid/non-existent grant rejects cleanly
            Console.WriteLine("\n[Test H] issue_lsa_ticket on non-existent grant");
            var reqIdH = Guid.NewGuid().ToString("N");
            var ticketRespH = await SendIpcCommandAsync(new LocalAuthRequest(1, "issue_lsa_ticket", reqIdH, null, null, "S-1-5-21-0000", "TEST\\User"));
            Check(ticketRespH != null && ticketRespH.status == LocalAuthStatus.NotFound, "TEST H: issue_lsa_ticket on non-existent grant returns NotFound");

            // TEST I: issue_lsa_ticket on approved grant issues valid ticket
            Console.WriteLine("\n[Test I] issue_lsa_ticket on approved grant");
            var reqIdI = Guid.NewGuid().ToString("N");
            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            worker.InjectApprovedGrantForTesting(reqIdI, "S-1-5-21-12345-67890", "TEST\\ValidUser", "device-test-001", nowSec + 30);
            var ticketRespI = await SendIpcCommandAsync(new LocalAuthRequest(1, "issue_lsa_ticket", reqIdI, null, null, "S-1-5-21-12345-67890", "TEST\\ValidUser"));
            Check(ticketRespI != null && ticketRespI.status == LocalAuthStatus.Approved && !string.IsNullOrWhiteSpace(ticketRespI.ticket),
                "TEST I: issue_lsa_ticket returns Approved status and base64 ticket", $"status={ticketRespI?.status} msg={ticketRespI?.message} ticketLen={ticketRespI?.ticket?.Length}");

            // TEST J: issue_lsa_ticket with wrong SID is rejected
            Console.WriteLine("\n[Test J] issue_lsa_ticket with wrong SID");
            var reqIdJ = Guid.NewGuid().ToString("N");
            worker.InjectApprovedGrantForTesting(reqIdJ, "S-1-5-21-11111-22222", "TEST\\UserJ", "device-test-001", nowSec + 30);
            var ticketRespJ = await SendIpcCommandAsync(new LocalAuthRequest(1, "issue_lsa_ticket", reqIdJ, null, null, "S-1-5-21-WRONG-SID", "TEST\\UserJ"));
            Check(ticketRespJ != null && ticketRespJ.status == LocalAuthStatus.Rejected,
                "TEST J: issue_lsa_ticket with wrong SID is rejected with Rejected status", $"status={ticketRespJ?.status}");

            // TEST K: issue_lsa_ticket on expired grant is rejected
            Console.WriteLine("\n[Test K] issue_lsa_ticket on expired grant");
            var reqIdK = Guid.NewGuid().ToString("N");
            worker.InjectApprovedGrantForTesting(reqIdK, "S-1-5-21-12345-67890", "TEST\\UserK", "device-test-001", nowSec - 10);
            var ticketRespK = await SendIpcCommandAsync(new LocalAuthRequest(1, "issue_lsa_ticket", reqIdK, null, null, "S-1-5-21-12345-67890", "TEST\\UserK"));
            Check(ticketRespK != null && ticketRespK.status == LocalAuthStatus.Expired,
                "TEST K: issue_lsa_ticket on expired grant returns Expired status", $"status={ticketRespK?.status}");

            // TEST M: reserve_grant & consume_grant for Shell
            Console.WriteLine("\n[Test M] Shell reserve_grant and consume_grant");
            var reqIdM = Guid.NewGuid().ToString("N");
            RegisterShellRequest(reqIdM, "S-1-5-21-99999", 2);
            await testBluetoothLeases.EnsureEnabledAsync(reqIdM);
            worker.InjectApprovedGrantForTesting(reqIdM, "S-1-5-21-99999", "TEST\\ShellUser", "device-test-shell", nowSec + 30, sessionId: 2, clientType: "shell");
            
            var reserveRespM = await SendIpcCommandAsync(new LocalAuthRequest(1, "reserve_grant", reqIdM, null, null, "S-1-5-21-99999", "TEST\\ShellUser", session_id: 2, client_type: "shell", process_id: clientProcessId));
            Check(reserveRespM != null && reserveRespM.status == LocalAuthStatus.Reserved, "TEST M: Shell reserve_grant succeeds", $"status={reserveRespM?.status}");
            Check(!gateAuthority.GetSnapshot(2, "S-1-5-21-99999").ExplorerAllowed, "TEST M: Reserved but not consumed does not authorize Explorer");

            var consumeRespM = await SendIpcCommandAsync(new LocalAuthRequest(1, "consume_grant", reqIdM, null, null, "S-1-5-21-99999", "TEST\\ShellUser", session_id: 2, client_type: "shell", process_id: clientProcessId));
            Check(consumeRespM != null && consumeRespM.status == LocalAuthStatus.Consumed, "TEST M: Shell consume_grant succeeds", $"status={consumeRespM?.status}");
            for (var i = 0; i < 20 && testRadio.State != BluetoothState.Disabled; i++) await Task.Delay(10);
            Check(testRadio.State == BluetoothState.Disabled && testRadio.DisableCalls == 1,
                "TEST M: Consumed grant restores FaceUnlock-owned Bluetooth OFF");

            // TEST N: Shell consume_grant on already consumed grant (replay) is rejected
            Console.WriteLine("\n[Test N] Shell consume_grant replay rejection");
            var consumeRespN = await SendIpcCommandAsync(new LocalAuthRequest(1, "consume_grant", reqIdM, null, null, "S-1-5-21-99999", "TEST\\ShellUser", session_id: 2, client_type: "shell", process_id: clientProcessId));
            Check(consumeRespN != null && (consumeRespN.status == LocalAuthStatus.NotFound || consumeRespN.status == LocalAuthStatus.Rejected),
                "TEST N: Replayed consume_grant returns NotFound/Rejected", $"status={consumeRespN?.status}");

            // TEST O: Shell reserve_grant with wrong SID is rejected
            Console.WriteLine("\n[Test O] Shell reserve_grant with wrong SID");
            var reqIdO = Guid.NewGuid().ToString("N");
            RegisterShellRequest(reqIdO, "S-1-5-21-11111", 11);
            worker.InjectApprovedGrantForTesting(reqIdO, "S-1-5-21-11111", "TEST\\UserO", "device-test-001", nowSec + 30, sessionId: 11, clientType: "shell");
            var reserveRespO = await SendIpcCommandAsync(new LocalAuthRequest(1, "reserve_grant", reqIdO, null, null, "S-1-5-21-WRONG-SID", "TEST\\UserO", session_id: 11, client_type: "shell", process_id: clientProcessId));
            Check(reserveRespO != null && reserveRespO.status == LocalAuthStatus.Rejected,
                "TEST O: reserve_grant with wrong SID is rejected", $"status={reserveRespO?.status}");

            // TEST P: Shell reserve_grant with wrong Session ID is rejected
            Console.WriteLine("\n[Test P] Shell reserve_grant with wrong Session ID");
            var reqIdP = Guid.NewGuid().ToString("N");
            RegisterShellRequest(reqIdP, "S-1-5-21-22222", 12);
            worker.InjectApprovedGrantForTesting(reqIdP, "S-1-5-21-22222", "TEST\\UserP", "device-test-001", nowSec + 30, sessionId: 12, clientType: "shell");
            var reserveRespP = await SendIpcCommandAsync(new LocalAuthRequest(1, "reserve_grant", reqIdP, null, null, "S-1-5-21-22222", "TEST\\UserP", session_id: 999, client_type: "shell", process_id: clientProcessId));
            Check(reserveRespP != null && reserveRespP.status == LocalAuthStatus.Rejected,
                "TEST P: reserve_grant with wrong Session ID is rejected", $"status={reserveRespP?.status}");

            // TEST Q: Shell consume_grant with wrong Session ID is rejected
            Console.WriteLine("\n[Test Q] Shell consume_grant with wrong Session ID");
            var reqIdQ = Guid.NewGuid().ToString("N");
            RegisterShellRequest(reqIdQ, "S-1-5-21-33333", 13);
            worker.InjectApprovedGrantForTesting(reqIdQ, "S-1-5-21-33333", "TEST\\UserQ", "device-test-001", nowSec + 30, sessionId: 13, clientType: "shell");
            var consumeRespQ = await SendIpcCommandAsync(new LocalAuthRequest(1, "consume_grant", reqIdQ, null, null, "S-1-5-21-33333", "TEST\\UserQ", session_id: 888, client_type: "shell", process_id: clientProcessId));
            Check(consumeRespQ != null && consumeRespQ.status == LocalAuthStatus.Rejected,
                "TEST Q: consume_grant with wrong Session ID is rejected", $"status={consumeRespQ?.status}");

            // TEST R: Shell reserve_grant on expired grant
            Console.WriteLine("\n[Test R] Shell reserve_grant on expired grant");
            var reqIdR = Guid.NewGuid().ToString("N");
            RegisterShellRequest(reqIdR, "S-1-5-21-44444", 14);
            worker.InjectApprovedGrantForTesting(reqIdR, "S-1-5-21-44444", "TEST\\UserR", "device-test-001", nowSec - 5, sessionId: 14, clientType: "shell");
            var reserveRespR = await SendIpcCommandAsync(new LocalAuthRequest(1, "reserve_grant", reqIdR, null, null, "S-1-5-21-44444", "TEST\\UserR", session_id: 14, client_type: "shell", process_id: clientProcessId));
            Check(reserveRespR != null && (reserveRespR.status == LocalAuthStatus.Expired || reserveRespR.status == LocalAuthStatus.NotFound),
                "TEST R: reserve_grant on expired grant returns Expired or NotFound", $"status={reserveRespR?.status}");

            // TEST S: unapproved pending grant cannot be promoted by reserve_grant
            Console.WriteLine("\n[Test S] Shell reserve_grant on unapproved grant");
            var reqIdS = Guid.NewGuid().ToString("N");
            RegisterShellRequest(reqIdS, "S-1-5-21-55555", 15);
            worker.InjectPendingGrantForTesting(reqIdS, "S-1-5-21-55555", 15);
            var reserveRespS = await SendIpcCommandAsync(new LocalAuthRequest(1, "reserve_grant", reqIdS, user_sid: "S-1-5-21-55555", session_id: 15, client_type: "shell", process_id: clientProcessId));
            Check(reserveRespS != null && reserveRespS.status == LocalAuthStatus.Rejected,
                "TEST S: unapproved grant cannot be reserved or authorize desktop", $"status={reserveRespS?.status}");
            Check(!gateAuthority.GetSnapshot(15, "S-1-5-21-55555").ExplorerAllowed, "TEST S: Explorer remains denied");
        }
        finally
        {
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);
        }

        Console.WriteLine("\n============================================================");
        Console.WriteLine($"  IPC TEST RESULTS: {passed} passed, {failed} failed");
        Console.WriteLine("============================================================");

        return (failed == 0) ? 0 : 1;
    }
}
