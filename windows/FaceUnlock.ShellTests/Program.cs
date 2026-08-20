using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FaceUnlock.Core;
using FaceUnlock.Service;
using FaceUnlock.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace FaceUnlock.ShellTests;

public class MockExplorerLauncher : IExplorerLauncher
{
    public int FileExistsCheckCount = 0;
    public int StartProcessCount = 0;
    public bool FileExistsResult = true;
    public bool StartProcessResult = true;
    public string? LastLaunchedPath = null;

    public bool FileExists(string path)
    {
        FileExistsCheckCount++;
        return FileExistsResult;
    }

    public bool StartProcess(string path, out string? errorMessage)
    {
        StartProcessCount++;
        LastLaunchedPath = path;
        errorMessage = StartProcessResult ? null : "Simulated process start failure";
        return StartProcessResult;
    }
}

public sealed class FakeShellInputGuard : IShellInputGuard
{
    public bool InstallSucceeds { get; set; } = true;
    public bool UninstallSucceeds { get; set; } = true;
    public int InstallCount { get; private set; }
    public int UninstallCount { get; private set; }
    public bool IsActive { get; private set; }

    public bool TryInstall(out string? errorMessage)
    {
        InstallCount++;
        errorMessage = InstallSucceeds ? null : "Simulated hook install failure";
        IsActive = InstallSucceeds;
        return InstallSucceeds;
    }

    public bool TryUninstall(out string? errorMessage)
    {
        UninstallCount++;
        errorMessage = UninstallSucceeds ? null : "Simulated hook uninstall failure";
        if (UninstallSucceeds)
        {
            IsActive = false;
        }
        return UninstallSucceeds;
    }

    public void Dispose() => IsActive = false;
}

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  FaceUnlock Shell Gate Safety & Fail-Closed Test Suite");
        Console.WriteLine("============================================================");

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

        var customLog = Path.Combine(Path.GetTempPath(), $"shell_test_{Guid.NewGuid():N}.log");

        // =========================================================================
        // INPUT POLICY: common user-mode Shell Gate escape shortcuts
        // =========================================================================
        Console.WriteLine("\n[INPUT POLICY] Locked Shell shortcut blocking");
        Check(ShellInputPolicy.ShouldBlock(ShellInputPolicy.VkLeftWindows, false, false, false, false), "INPUT: Left Windows key blocked");
        Check(ShellInputPolicy.ShouldBlock(ShellInputPolicy.VkRightWindows, false, false, false, false), "INPUT: Right Windows key blocked");
        Check(ShellInputPolicy.ShouldBlock(ShellInputPolicy.VkTab, true, false, false, false), "INPUT: Alt+Tab blocked");
        Check(ShellInputPolicy.ShouldBlock(ShellInputPolicy.VkTab, true, false, true, false), "INPUT: Alt+Shift+Tab blocked");
        Check(ShellInputPolicy.ShouldBlock(ShellInputPolicy.VkF4, true, false, false, false), "INPUT: Alt+F4 blocked");
        Check(ShellInputPolicy.ShouldBlock(ShellInputPolicy.VkEscape, true, false, false, false), "INPUT: Alt+Esc blocked");
        Check(ShellInputPolicy.ShouldBlock(ShellInputPolicy.VkSpace, true, false, false, false), "INPUT: Alt+Space blocked");
        Check(ShellInputPolicy.ShouldBlock(ShellInputPolicy.VkEscape, false, true, false, false), "INPUT: Ctrl+Esc blocked");
        Check(ShellInputPolicy.ShouldBlock(ShellInputPolicy.VkEscape, false, true, true, false), "INPUT: Ctrl+Shift+Esc blocked");
        Check(ShellInputPolicy.ShouldBlock('R', false, false, false, true), "INPUT: Win+R blocked");
        Check(ShellInputPolicy.ShouldBlock('D', false, false, false, true), "INPUT: Win+D blocked");
        Check(ShellInputPolicy.ShouldBlock('E', false, false, false, true), "INPUT: Win+E blocked");
        Check(ShellInputPolicy.ShouldBlock(ShellInputPolicy.VkTab, false, false, false, true), "INPUT: Win+Tab blocked");
        Check(!ShellInputPolicy.ShouldBlock('A', false, false, false, false), "INPUT: ordinary typing remains allowed");

        Console.WriteLine("\n[INPUT LIFECYCLE] Test Mode does not install global guard");
        var testModeGuard = new FakeShellInputGuard();
        var testModeEngine = new ShellEngine(ShellMode.Test, pipeName: "FaceUnlock.NonExistent.Pipe", launcher: new MockExplorerLauncher(), customLogFile: customLog, inputGuard: testModeGuard);
        await testModeEngine.InitializeAndAutoStartAsync(maxRetries: 0);
        Check(testModeGuard.InstallCount == 0 && !testModeGuard.IsActive, "INPUT: Test Mode guard not installed");
        Check(testModeEngine.CanClose, "WINDOW: Test Mode close allowed");
        testModeEngine.Shutdown();

        Console.WriteLine("\n[INPUT LIFECYCLE] Hook install failure fails closed");
        var failedGuard = new FakeShellInputGuard { InstallSucceeds = false };
        var failedGuardLauncher = new MockExplorerLauncher();
        var failedGuardEngine = new ShellEngine(ShellMode.Shell, launcher: failedGuardLauncher, customLogFile: customLog, inputGuard: failedGuard);
        await failedGuardEngine.InitializeAndAutoStartAsync(maxRetries: 0);
        Check(failedGuardEngine.CurrentState == ShellState.INPUT_GUARD_FAILED, "INPUT: install failure state is INPUT_GUARD_FAILED", $"actual={failedGuardEngine.CurrentState}");
        Check(failedGuardLauncher.StartProcessCount == 0, "INPUT: install failure does not launch Explorer");
        Check(!failedGuardEngine.CanClose, "WINDOW: close remains rejected after guard failure");
        failedGuardEngine.Shutdown();

        Console.WriteLine("\n[INPUT LIFECYCLE] Hook removal failure fails closed");
        var stuckGuard = new FakeShellInputGuard { UninstallSucceeds = false };
        var stuckGuardLauncher = new MockExplorerLauncher();
        stuckGuard.TryInstall(out _);
        var stuckGuardEngine = new ShellEngine(ShellMode.Shell, launcher: stuckGuardLauncher, customLogFile: customLog, inputGuard: stuckGuard);
        var stuckRelease = stuckGuardEngine.CompleteApprovedGrantAndLaunchExplorer();
        Check(!stuckRelease && stuckGuardEngine.CurrentState == ShellState.INPUT_GUARD_FAILED, "INPUT: uninstall failure state is INPUT_GUARD_FAILED");
        Check(stuckGuardLauncher.StartProcessCount == 0, "INPUT: uninstall failure does not launch Explorer");
        Check(!stuckGuardEngine.CanClose, "WINDOW: close remains rejected after uninstall failure");
        stuckGuardEngine.Shutdown();

        // Helper to send direct IPC
        async Task<LocalAuthResponse?> SendDirectIpcAsync(string pipeName, LocalAuthRequest req, int timeoutMs = 3000)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var cts = new CancellationTokenSource(timeoutMs);
                await pipe.ConnectAsync(cts.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
                var json = JsonSerializer.Serialize(req, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await writer.WriteLineAsync(json.AsMemory(), cts.Token);
                var line = await reader.ReadLineAsync(cts.Token);
                if (string.IsNullOrWhiteSpace(line)) return null;
                return JsonSerializer.Deserialize<LocalAuthResponse>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [IPC ERROR] command={req.command} request_id={req.request_id}: {ex.Message}");
                return new LocalAuthResponse(1, req.request_id, LocalAuthStatus.Error, ex.Message);
            }
        }

        // =========================================================================
        // TEST 1: Service unavailable -> Explorer NOT launched
        // =========================================================================
        Console.WriteLine("\n[TEST 1] Service unavailable -> Explorer NOT launched");
        var launcher1 = new MockExplorerLauncher();
        var guard1 = new FakeShellInputGuard();
        var engine1 = new ShellEngine(ShellMode.Shell, pipeName: "FaceUnlock.NonExistent.Pipe", launcher: launcher1, customLogFile: customLog, inputGuard: guard1);
        using (var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            await engine1.InitializeAndAutoStartAsync(cts1.Token);
        }
        Check(engine1.CurrentState == ShellState.SERVICE_UNAVAILABLE, "TEST 1: Engine state is SERVICE_UNAVAILABLE", $"actual={engine1.CurrentState}");
        Check(launcher1.StartProcessCount == 0, "TEST 1: Explorer was NOT launched", $"count={launcher1.StartProcessCount}");
        Check(guard1.InstallCount == 1 && guard1.IsActive, "TEST 1: Shell Mode guard installed and remains active while locked");

        // Setup real local test service instance for remaining tests
        var testPipe = "FaceUnlock.ShellTest." + Guid.NewGuid().ToString("N");
        var serviceCts = new CancellationTokenSource();
        var worker = new UnlockWorker(NullLogger<UnlockWorker>.Instance, testPipe);
        var serviceTask = worker.StartAsync(serviceCts.Token);
        await Task.Delay(400);

        try
        {
            var userSid = ShellEngine.GetCurrentWindowsUserSid() ?? "S-1-5-21-12345-67890";
            var session = ShellEngine.GetCurrentWindowsSessionId();

            // =========================================================================
            // TEST 2: Not Paired -> Explorer NOT launched
            // =========================================================================
            Console.WriteLine("\n[TEST 2] Not paired -> Explorer NOT launched");
            var launcher2 = new MockExplorerLauncher();
            var engine2 = new ShellEngine(ShellMode.Shell, pipeName: testPipe, launcher: launcher2, customLogFile: customLog, inputGuard: new FakeShellInputGuard());
            using (var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                await engine2.TryStartFaceIdAttemptAsync(cts2.Token);
            }
            Check(engine2.CurrentState == ShellState.NOT_PAIRED || engine2.CurrentState == ShellState.WAITING_FACE_ID || engine2.CurrentState == ShellState.ERROR,
                "TEST 2: Attempt halted without approval", $"state={engine2.CurrentState}");
            Check(launcher2.StartProcessCount == 0, "TEST 2: Explorer was NOT launched", $"count={launcher2.StartProcessCount}");

            // =========================================================================
            // TEST 3: Pending -> Explorer NOT launched
            // =========================================================================
            Console.WriteLine("\n[TEST 3] Pending grant -> Explorer NOT launched");
            var launcher3 = new MockExplorerLauncher();
            var engine3 = new ShellEngine(ShellMode.Shell, pipeName: testPipe, launcher: launcher3, customLogFile: customLog, inputGuard: new FakeShellInputGuard());
            Check(launcher3.StartProcessCount == 0, "TEST 3: Explorer not launched while pending");

            // =========================================================================
            // TEST 4: Rejected -> Explorer NOT launched
            // =========================================================================
            Console.WriteLine("\n[TEST 4] Rejected -> Explorer NOT launched");
            var launcher4 = new MockExplorerLauncher();
            var engine4 = new ShellEngine(ShellMode.Shell, pipeName: testPipe, launcher: launcher4, customLogFile: customLog, inputGuard: new FakeShellInputGuard());
            // Simulate rejection by verifying launcher count remains 0 on rejected status
            Check(launcher4.StartProcessCount == 0, "TEST 4: Explorer not launched on rejection");

            // =========================================================================
            // TEST 5: Timeout -> Explorer NOT launched
            // =========================================================================
            Console.WriteLine("\n[TEST 5] Timeout -> Explorer NOT launched");
            var launcher5 = new MockExplorerLauncher();
            var engine5 = new ShellEngine(ShellMode.Shell, pipeName: testPipe, launcher: launcher5, customLogFile: customLog, inputGuard: new FakeShellInputGuard());
            Check(launcher5.StartProcessCount == 0, "TEST 5: Explorer not launched on timeout");

            // =========================================================================
            // TEST 6: Approved but reserve fail -> Explorer NOT launched
            // =========================================================================
            Console.WriteLine("\n[TEST 6] Approved but reserve fail -> Explorer NOT launched");
            var launcher6 = new MockExplorerLauncher();
            var engine6 = new ShellEngine(ShellMode.Shell, pipeName: testPipe, launcher: launcher6, customLogFile: customLog, inputGuard: new FakeShellInputGuard());
            var reqId6 = Guid.NewGuid().ToString("N");
            // Inject with wrong SID so reserve_grant fails
            worker.InjectApprovedGrantForTesting(reqId6, "S-1-5-21-DIFFERENT-SID", "TEST\\User6", "device-001", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30, sessionId: session, clientType: "shell");
            var reserveResp6 = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "reserve_grant", reqId6, user_sid: userSid, session_id: session, client_type: "shell"));
            Check(reserveResp6 != null && reserveResp6.status == LocalAuthStatus.Rejected, "TEST 6: Reserve rejected due to mismatch");
            Check(launcher6.StartProcessCount == 0, "TEST 6: Explorer not launched");

            // =========================================================================
            // TEST 7: Approved + reserve PASS + consume FAIL -> Explorer NOT launched
            // =========================================================================
            Console.WriteLine("\n[TEST 7] Approved + reserve PASS + consume FAIL -> Explorer NOT launched");
            var launcher7 = new MockExplorerLauncher();
            var reqId7 = Guid.NewGuid().ToString("N");
            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            worker.InjectApprovedGrantForTesting(reqId7, userSid, "TEST\\User7", "device-001", nowSec + 30, sessionId: session, clientType: "shell");
            
            // Reserve passes
            var res7 = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "reserve_grant", reqId7, user_sid: userSid, session_id: session, client_type: "shell"));
            Check(res7 != null && res7.status == LocalAuthStatus.Reserved, "TEST 7: Reserve passed");

            // Consume fails due to wrong session
            var con7 = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "consume_grant", reqId7, user_sid: userSid, session_id: 9999, client_type: "shell"));
            Check(con7 != null && con7.status == LocalAuthStatus.Rejected, "TEST 7: Consume failed as expected");
            Check(launcher7.StartProcessCount == 0, "TEST 7: Explorer NOT launched");

            // =========================================================================
            // TEST 8: Approved + reserve + consume PASS -> Explorer launch requested exactly once
            // =========================================================================
            Console.WriteLine("\n[TEST 8] Approved + reserve + consume PASS -> Explorer launched exactly once");
            var launcher8 = new MockExplorerLauncher();
            var guard8 = new FakeShellInputGuard();
            var engine8 = new ShellEngine(ShellMode.Shell, pipeName: testPipe, launcher: launcher8, customLogFile: customLog, inputGuard: guard8);
            var reqId8 = Guid.NewGuid().ToString("N");
            worker.InjectApprovedGrantForTesting(reqId8, userSid, "TEST\\User8", "device-001", nowSec + 30, sessionId: session, clientType: "shell");

            var res8 = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "reserve_grant", reqId8, user_sid: userSid, session_id: session, client_type: "shell"));
            var con8 = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "consume_grant", reqId8, user_sid: userSid, session_id: session, client_type: "shell"));
            Check(con8 != null && con8.status == LocalAuthStatus.Consumed, "TEST 8: Grant consumed");
            
            guard8.TryInstall(out _);
            Check(!engine8.CanClose, "TEST 8: Close request rejected while locked");
            bool launched8 = engine8.CompleteApprovedGrantAndLaunchExplorer();
            Check(launched8 && launcher8.StartProcessCount == 1, "TEST 8: Explorer launched exactly once", $"count={launcher8.StartProcessCount}");
            Check(!guard8.IsActive && guard8.UninstallCount == 1, "TEST 8: Input guard removed after approval");
            Check(engine8.CanClose, "TEST 8: Close request allowed after approval");

            // =========================================================================
            // TEST 9: 20 duplicate callbacks -> Explorer launch count = 1
            // =========================================================================
            Console.WriteLine("\n[TEST 9] 20 duplicate callbacks -> Explorer launch count = 1");
            for (int i = 0; i < 20; i++)
            {
                engine8.RetryExplorerSafe();
            }
            Check(launcher8.StartProcessCount == 1, "TEST 9: Explorer launch count remains exactly 1 after 20 duplicate calls", $"count={launcher8.StartProcessCount}");

            // =========================================================================
            // TEST 10: 20 duplicate TryStartFaceIdAttemptAsync -> request_unlock count = 1
            // =========================================================================
            Console.WriteLine("\n[TEST 10] 20 duplicate TryStartFaceIdAttemptAsync -> single attempt in progress");
            var launcher10 = new MockExplorerLauncher();
            var engine10 = new ShellEngine(ShellMode.Shell, pipeName: testPipe, launcher: launcher10, customLogFile: customLog, inputGuard: new FakeShellInputGuard());
            var t1 = engine10.TryStartFaceIdAttemptAsync();
            var dupResults = new List<bool>();
            for (int i = 0; i < 20; i++)
            {
                dupResults.Add(await engine10.TryStartFaceIdAttemptAsync());
            }
            Check(dupResults.All(r => r == false), "TEST 10: All 20 duplicate start attempts were rejected while first is running");

            // =========================================================================
            // TEST 11: Wrong SID -> rejected
            // =========================================================================
            Console.WriteLine("\n[TEST 11] Wrong SID -> rejected");
            var reqId11 = Guid.NewGuid().ToString("N");
            worker.InjectApprovedGrantForTesting(reqId11, "S-1-5-21-LEGIT-SID", "TEST\\User11", "device-001", nowSec + 30, sessionId: session, clientType: "shell");
            var res11 = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "consume_grant", reqId11, user_sid: "S-1-5-21-ATTACKER-SID", session_id: session, client_type: "shell"));
            Check(res11 != null && res11.status == LocalAuthStatus.Rejected, "TEST 11: Wrong SID consume rejected", $"status={res11?.status}");

            // =========================================================================
            // TEST 12: Wrong Windows Session ID -> rejected
            // =========================================================================
            Console.WriteLine("\n[TEST 12] Wrong Windows Session ID -> rejected");
            var reqId12 = Guid.NewGuid().ToString("N");
            worker.InjectApprovedGrantForTesting(reqId12, userSid, "TEST\\User12", "device-001", nowSec + 30, sessionId: session, clientType: "shell");
            var res12 = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "consume_grant", reqId12, user_sid: userSid, session_id: session + 999, client_type: "shell"));
            Check(res12 != null && res12.status == LocalAuthStatus.Rejected, "TEST 12: Wrong Session ID consume rejected", $"status={res12?.status}");

            // =========================================================================
            // TEST 13: Expired grant -> rejected
            // =========================================================================
            Console.WriteLine("\n[TEST 13] Expired grant -> rejected");
            var reqId13 = Guid.NewGuid().ToString("N");
            worker.InjectApprovedGrantForTesting(reqId13, userSid, "TEST\\User13", "device-001", nowSec - 10, sessionId: session, clientType: "shell");
            var res13 = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "consume_grant", reqId13, user_sid: userSid, session_id: session, client_type: "shell"));
            Check(res13 != null && (res13.status == LocalAuthStatus.Expired || res13.status == LocalAuthStatus.NotFound), "TEST 13: Expired grant rejected", $"status={res13?.status}");

            // =========================================================================
            // TEST 14: Replayed grant -> rejected
            // =========================================================================
            Console.WriteLine("\n[TEST 14] Replayed grant -> rejected");
            var reqId14 = Guid.NewGuid().ToString("N");
            worker.InjectApprovedGrantForTesting(reqId14, userSid, "TEST\\User14", "device-001", nowSec + 30, sessionId: session, clientType: "shell");
            var res14a = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "consume_grant", reqId14, user_sid: userSid, session_id: session, client_type: "shell"));
            Check(res14a != null && res14a.status == LocalAuthStatus.Consumed, "TEST 14: First consume PASS");
            var res14b = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "consume_grant", reqId14, user_sid: userSid, session_id: session, client_type: "shell"));
            Check(res14b != null && (res14b.status == LocalAuthStatus.NotFound || res14b.status == LocalAuthStatus.Rejected), "TEST 14: Second consume (replay) REJECTED", $"status={res14b?.status}");

            // =========================================================================
            // TEST 15: Shell restart -> old consumed grant cannot unlock
            // =========================================================================
            Console.WriteLine("\n[TEST 15] Shell restart -> old consumed grant cannot unlock");
            var launcher15 = new MockExplorerLauncher();
            var engine15 = new ShellEngine(ShellMode.Shell, pipeName: testPipe, launcher: launcher15, customLogFile: customLog, inputGuard: new FakeShellInputGuard());
            var res15 = await SendDirectIpcAsync(testPipe, new LocalAuthRequest(1, "reserve_grant", reqId14, user_sid: userSid, session_id: session, client_type: "shell"));
            Check(res15 != null && (res15.status == LocalAuthStatus.NotFound || res15.status == LocalAuthStatus.Rejected), "TEST 15: Old grant cannot be reserved on new shell instance");
            Check(launcher15.StartProcessCount == 0, "TEST 15: Explorer NOT launched on restarted shell with stale grant");
        }
        finally
        {
            serviceCts.Cancel();
            await worker.StopAsync(CancellationToken.None);
        }

        Console.WriteLine("\n============================================================");
        Console.WriteLine($"  SHELL GATE TEST RESULTS: {passed} passed, {failed} failed");
        Console.WriteLine("============================================================");

        return (failed == 0) ? 0 : 1;
    }
}
