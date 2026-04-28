using System.Runtime.InteropServices;
using System.Text;

// ── Test harness ──
Console.WriteLine("=== Everything SDK Test Harness ===");
Console.WriteLine();

// Test 1: Check if Everything is running / DB loaded
Console.Write("IsDBLoaded: ");
try
{
    bool loaded = Everything.Everything_IsDBLoaded();
    Console.WriteLine(loaded ? "YES" : "NO");
    if (!loaded)
    {
        Console.WriteLine("  ERROR: Everything DB not loaded. Is Everything running?");
        Console.WriteLine($"  GetLastError: {Everything.Everything_GetLastError()}");
    }
}
catch (DllNotFoundException ex)
{
    Console.WriteLine($"FAIL - DLL not found: {ex.Message}");
    return;
}

// Test 2: Get Everything version
try
{
    uint major = Everything.Everything_GetMajorVersion();
    uint minor = Everything.Everything_GetMinorVersion();
    uint rev = Everything.Everything_GetRevision();
    Console.WriteLine($"Everything version: {major}.{minor}.{rev}");
}
catch (Exception ex)
{
    Console.WriteLine($"Version check failed: {ex.Message}");
}

Console.WriteLine();

// ── Helper to run a query and print results ──
void RunQuery(string label, string searchText, bool matchPath = false, uint max = 10)
{
    Console.WriteLine($"--- Test: {label} ---");
    Console.WriteLine($"  Search string: \"{searchText}\"");
    Console.WriteLine($"  MatchPath: {matchPath}");

    Everything.Everything_Reset();
    Everything.Everything_SetSearchW(searchText);
    Everything.Everything_SetMatchPath(matchPath);
    Everything.Everything_SetMatchCase(false);
    Everything.Everything_SetRequestFlags(Everything.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME);
    Everything.Everything_SetSort(3); // path ascending
    Everything.Everything_SetMax(max);

    bool ok = Everything.Everything_QueryW(true);
    uint err = Everything.Everything_GetLastError();
    Console.WriteLine($"  Query returned: {ok}, LastError: {err}");

    if (!ok)
    {
        Console.WriteLine($"  QUERY FAILED!");
        Console.WriteLine();
        return;
    }

    uint numResults = Everything.Everything_GetNumResults();
    uint totResults = Everything.Everything_GetTotResults();
    Console.WriteLine($"  NumResults: {numResults}, TotResults: {totResults}");

    var sb = new StringBuilder(1024);
    for (uint i = 0; i < Math.Min(numResults, 5); i++)
    {
        bool isFile = Everything.Everything_IsFileResult(i);
        bool isFolder = Everything.Everything_IsFolderResult(i);
        sb.Clear();
        Everything.Everything_GetResultFullPathNameW(i, sb, (uint)sb.Capacity);
        Console.WriteLine($"  [{i}] {(isFile ? "FILE" : isFolder ? "DIR " : "??? ")} {sb}");
    }
    if (numResults > 5) Console.WriteLine($"  ... and {numResults - 5} more");
    Console.WriteLine();
}

// Test 3: Simplest possible query — just search for *.cs
RunQuery("Simple wildcard (*.cs)", "*.cs");

// Test 4: Search for files under a specific path using path with trailing backslash
RunQuery("Path with trailing backslash", @"D:\agentRansackAlternative\", matchPath: false);

// Test 5: Same with matchPath=true
RunQuery("Path with trailing backslash + MatchPath", @"D:\agentRansackAlternative\", matchPath: true);

// Test 6: Using quoted path with trailing backslash
RunQuery("Quoted path", "\"D:\\agentRansackAlternative\\\"", matchPath: false);

// Test 7: Using parent: operator (from the docs: "parent:c:\windows")
RunQuery("parent: operator", "parent:D:\\agentRansackAlternative", matchPath: false);

// Test 8: path with ext filter (like our app does)
RunQuery("Path + ext filter", @"D:\agentRansackAlternative\ ext:cs", matchPath: false);

// Test 9: Just the directory name without drive
RunQuery("Just folder name", "agentRansackAlternative", matchPath: false);

// Test 10: the folder: operator (should only match folders, NOT files)
RunQuery("folder: operator (wrong usage)", "folder:\"D:\\agentRansackAlternative\"", matchPath: false);

// Test 11: file: modifier with path
RunQuery("file: + path", "file: D:\\agentRansackAlternative\\", matchPath: false);

Console.WriteLine("=== Done ===");

// ── Raw DllImport bindings (classic, no source-gen) ──
static class Everything
{
    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    public static extern void Everything_SetSearchW(string lpSearchString);

    [DllImport("Everything64.dll")]
    public static extern void Everything_SetMatchPath(bool bEnable);

    [DllImport("Everything64.dll")]
    public static extern void Everything_SetMatchCase(bool bEnable);

    [DllImport("Everything64.dll")]
    public static extern void Everything_SetMax(uint dwMax);

    [DllImport("Everything64.dll")]
    public static extern void Everything_SetRequestFlags(uint dwRequestFlags);

    [DllImport("Everything64.dll")]
    public static extern void Everything_SetSort(uint dwSort);

    [DllImport("Everything64.dll")]
    public static extern void Everything_Reset();

    [DllImport("Everything64.dll")]
    public static extern void Everything_CleanUp();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    public static extern bool Everything_QueryW(bool bWait);

    [DllImport("Everything64.dll")]
    public static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll")]
    public static extern uint Everything_GetTotResults();

    [DllImport("Everything64.dll")]
    public static extern uint Everything_GetLastError();

    [DllImport("Everything64.dll")]
    public static extern bool Everything_IsDBLoaded();

    [DllImport("Everything64.dll")]
    public static extern bool Everything_IsFileResult(uint nIndex);

    [DllImport("Everything64.dll")]
    public static extern bool Everything_IsFolderResult(uint nIndex);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    public static extern uint Everything_GetResultFullPathNameW(uint nIndex, StringBuilder lpString, uint nMaxCount);

    [DllImport("Everything64.dll")]
    public static extern uint Everything_GetMajorVersion();

    [DllImport("Everything64.dll")]
    public static extern uint Everything_GetMinorVersion();

    [DllImport("Everything64.dll")]
    public static extern uint Everything_GetRevision();

    public const uint EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
    public const uint EVERYTHING_REQUEST_PATH = 0x00000002;
    public const uint EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME = 0x00000004;
}
