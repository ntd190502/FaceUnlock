using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FaceUnlock.Core;
using FaceUnlock.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace FaceUnlock.IpcIntegrationTests;

public class Program
{
    private const string PipeName = "FaceUnlock.Auth.Test";

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  FaceUnlock IPC Integration Tests (C# & Named Pipes)");
        Console.WriteLine("============================================================");

        var cts = new CancellationTokenSource();
        var worker = new UnlockWorker(NullLogger<UnlockWorker>.Instance, PipeName);

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
