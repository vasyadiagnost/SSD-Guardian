using SsdGuardian.Core;
using SsdGuardian.Windows;

if (args is ["--live"])
    return await LiveChecks.Run();
if (args is ["--network-probe"])
    return await LiveChecks.SendLoopbackTraffic();

var created = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
const int guardianPid = 400;
var target = new ProcessIdentity(401, created);
const string thirdParty = @"C:\Program Files\Example\Worker.exe";
var tests = new (string Name, Action Run)[]
{
    ("ETW refuses a token without administrator rights", () =>
    {
        var failure = DiskWriteMonitor.GetPrivilegeFailure(false);
        Require(failure is { State: MonitorState.Unavailable }, "expected an unavailable status");
        Require(failure!.GetMessage(UiLanguage.English).Contains("administrator", StringComparison.OrdinalIgnoreCase),
            "expected an English explanation");
        Require(failure.GetMessage(UiLanguage.Russian).Contains("администратора", StringComparison.OrdinalIgnoreCase),
            "expected a Russian explanation");
        Require(DiskWriteMonitor.GetPrivilegeFailure(true) is null, "admin token rejected prematurely");
    }),
    ("Windows network paths are excluded before SSD attribution", () =>
    {
        var inventory = StorageInventory.Discover();
        string[] networkPaths =
        [
            @"\\server\share\sample.log", @"\\?\UNC\server\share\sample.log",
            @"\??\UNC\server\share\sample.log", @"\Device\Mup\server\share\sample.log",
            @"\Device\LanmanRedirector\server\share\sample.log"
        ];
        foreach (var path in networkPaths)
        {
            var mapped = inventory.MapPath(path);
            Require(mapped.IsNetwork && mapped.Disk is null && mapped.Path is null, "network path received a local mapping");
        }
    }),
    ("Critical, protected and unverifiable processes fail closed", () =>
    {
        Require(Denied(target, created, true, false, thirdParty), "critical process allowed");
        Require(Denied(target, created, false, true, thirdParty), "protected process allowed");
        Require(Denied(target, created, null, false, thirdParty), "unknown critical flag allowed");
        Require(Denied(target, created, false, null, thirdParty), "unknown protection flag allowed");
        Require(Denied(target, created, false, false, null), "unverified image allowed");
    }),
    ("Common Windows host and Guardian cannot be terminated", () =>
    {
        Require(Denied(target, created, false, false, @"C:\WINDOWS\System32\svchost.exe"), "service host allowed");
        Require(Denied(new ProcessIdentity(guardianPid, created), created, false, false, thirdParty), "Guardian allowed");
        Require(!ProcessTerminator.Terminate(new ProcessIdentity(Environment.ProcessId, created)).Success,
            "real self-target returned success");
    }),
    ("Reused PID and unavailable creation time fail closed", () =>
    {
        Require(Denied(target, created.AddTicks(1), false, false, thirdParty), "reused PID allowed");
        Require(Denied(target, null, false, false, thirdParty), "unverified identity allowed");
        Require(!Denied(target, created, false, false, thirdParty), "eligible target rejected");
    })
}.Concat(RecoveryChecks.Scenarios).ToArray();

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {error.Message}");
    }
}

Console.WriteLine($"Windows: {tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static bool Denied(ProcessIdentity selected, DateTimeOffset? currentCreation, bool? isCritical, bool? isProtected, string? currentPath) =>
    ProcessTerminator.GetSafetyFailure(selected, currentCreation, isCritical, isProtected, currentPath, guardianPid) is not null;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
