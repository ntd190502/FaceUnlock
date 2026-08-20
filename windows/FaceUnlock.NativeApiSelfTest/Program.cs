using FaceUnlock.Service;

if (!OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("The native API smoke test must run on a real Windows host.");
}

WindowsShellGateSystem.RunNativeApiSmokeTest();
Console.WriteLine("FaceUnlock F.2 native API smoke test PASS");
