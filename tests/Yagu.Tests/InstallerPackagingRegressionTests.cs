using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Yagu.Tests;

public sealed class InstallerPackagingRegressionTests
{
    [Theory]
    [InlineData("x86", 0x014C, false)]
    [InlineData("x86", 0x014C, true)]
    [InlineData("x64", 0x8664, false)]
    [InlineData("x64", 0x8664, true)]
    [InlineData("arm64", 0xAA64, false)]
    [InlineData("arm64", 0xAA64, true)]
    public async Task StagedArchitecture_AcceptsMatchingPayloads(string architecture, int machine, bool includeIndexWorker)
    {
        using var fixture = new ArchitectureFixture();
        fixture.WritePayload(machine, includeIndexWorker);

        var result = await fixture.RunAsync("Assert-YaguStagedPayloadArchitecture -StagingDir $env:YAGU_TEST_STAGING -Architecture $env:YAGU_TEST_ARCH", architecture);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains($"arch OK: Yagu.exe is {architecture}", result.Output);
        Assert.Contains($"arch OK: mcp-server\\Yagu.McpServer.exe is {architecture}", result.Output);
        Assert.Equal(includeIndexWorker, result.Output.Contains($"arch OK: index-worker\\Yagu.IndexWorker.exe is {architecture}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Yagu.exe", "x86", 0x8664, "x64")]
    [InlineData("mcp-server\\Yagu.McpServer.exe", "x64", 0xAA64, "arm64")]
    [InlineData("index-worker\\Yagu.IndexWorker.exe", "arm64", 0x014C, "x86")]
    public async Task StagedArchitecture_RejectsMismatchedPayload(string relativePath, string architecture, int wrongMachine, string actual)
    {
        using var fixture = new ArchitectureFixture();
        fixture.WritePayload(architecture switch { "x86" => 0x014C, "x64" => 0x8664, _ => 0xAA64 });
        fixture.WritePe(relativePath, wrongMachine);

        var result = await fixture.RunAsync("Assert-YaguStagedPayloadArchitecture -StagingDir $env:YAGU_TEST_STAGING -Architecture $env:YAGU_TEST_ARCH", architecture);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Staged {relativePath} is {actual} but this is the {architecture} installer.", result.Error);
        Assert.Contains("It would fail to start on the target machine.", result.Error);
        Assert.DoesNotContain($"arch OK: {relativePath}", result.Output);
    }

    [Theory]
    [InlineData("Yagu.exe")]
    [InlineData("mcp-server\\Yagu.McpServer.exe")]
    public async Task StagedArchitecture_RejectsMissingRequiredPayload(string relativePath)
    {
        using var fixture = new ArchitectureFixture();
        fixture.WritePayload(0x8664);
        File.Delete(Path.Combine(fixture.StagingDir, relativePath));

        var result = await fixture.RunAsync("Assert-YaguStagedPayloadArchitecture -StagingDir $env:YAGU_TEST_STAGING -Architecture $env:YAGU_TEST_ARCH", "x64");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Staged payload missing: {relativePath}", result.Error);
    }

    [Theory]
    [InlineData(0x014C, "x86", 0x40)]
    [InlineData(0x8664, "x64", 0x100)]
    [InlineData(0xAA64, "arm64", 0x200)]
    [InlineData(0x0000, "unknown(0x0000)", 0x80)]
    [InlineData(0x01C4, "unknown(0x01C4)", 0x80)]
    [InlineData(0xFFFF, "unknown(0xFFFF)", 0x80)]
    public async Task StagedArchitecture_ReadsMachineAtPeOffsetAndClosesImage(int machine, string expected, int peOffset)
    {
        using var fixture = new ArchitectureFixture();
        string image = fixture.WritePe("header fixture.exe", machine, peOffset);

        var result = await fixture.RunAsync(ArchitectureFixture.VerifyImageClosed("Get-YaguPeMachine -Path $env:YAGU_TEST_IMAGE"), imagePath: image);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(expected, result.Output.Trim());
    }

    [Theory]
    [InlineData("Yagu.exe", 0x0000, "unknown(0x0000)")]
    [InlineData("mcp-server\\Yagu.McpServer.exe", 0x01C4, "unknown(0x01C4)")]
    [InlineData("index-worker\\Yagu.IndexWorker.exe", 0xFFFF, "unknown(0xFFFF)")]
    public async Task StagedArchitecture_RejectsUnknownMachine(string relativePath, int machine, string actual)
    {
        using var fixture = new ArchitectureFixture();
        fixture.WritePayload(0x8664);
        fixture.WritePe(relativePath, machine);

        var result = await fixture.RunAsync("Assert-YaguStagedPayloadArchitecture -StagingDir $env:YAGU_TEST_STAGING -Architecture x64");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Staged {relativePath} is {actual} but this is the x64 installer.", result.Error);
    }

    [Theory]
    [InlineData("empty", "EndOfStreamException")]
    [InlineData("short-mz", "EndOfStreamException")]
    [InlineData("bad-mz", "Not a PE image:")]
    [InlineData("short-offset", "EndOfStreamException")]
    [InlineData("offset-zero", "Missing PE signature:")]
    [InlineData("offset-eof", "EndOfStreamException")]
    [InlineData("offset-max", "EndOfStreamException")]
    [InlineData("bad-pe", "Missing PE signature:")]
    [InlineData("short-signature", "EndOfStreamException")]
    [InlineData("short-machine", "EndOfStreamException")]
    public async Task StagedArchitecture_RejectsMalformedHeaderAndClosesImage(string corruption, string expectedError)
    {
        using var fixture = new ArchitectureFixture();
        fixture.WritePayload(0x8664);
        string image = Path.Combine(fixture.StagingDir, "mcp-server", "Yagu.McpServer.exe");
        using (var stream = File.Open(image, FileMode.Open, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            switch (corruption)
            {
                case "empty": stream.SetLength(0); break;
                case "short-mz": stream.SetLength(1); break;
                case "bad-mz": writer.Write((ushort)0); break;
                case "short-offset": stream.SetLength(0x3F); break;
                case "offset-zero": stream.Position = 0x3C; writer.Write(0u); break;
                case "offset-eof": stream.Position = 0x3C; writer.Write((uint)stream.Length); break;
                case "offset-max": stream.Position = 0x3C; writer.Write(uint.MaxValue); break;
                case "bad-pe": stream.Position = 0x80; writer.Write(0x00004551u); break;
                case "short-signature": stream.SetLength(0x83); break;
                case "short-machine": stream.SetLength(0x85); break;
                default: throw new ArgumentOutOfRangeException(nameof(corruption));
            }
        }

        var result = await fixture.RunAsync(ArchitectureFixture.VerifyImageClosed(
            "Assert-YaguStagedPayloadArchitecture -StagingDir $env:YAGU_TEST_STAGING -Architecture x64"), imagePath: image);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
        Assert.Contains("arch OK: Yagu.exe is x64", result.Output);
        Assert.DoesNotContain("arch OK: mcp-server", result.Output);
        Assert.DoesNotContain("arch OK: index-worker", result.Output);
    }

    [Fact]
    public async Task StagedArchitecture_ReportsMissingImage()
    {
        using var fixture = new ArchitectureFixture();

        var result = await fixture.RunAsync("Get-YaguPeMachine -Path $env:YAGU_TEST_IMAGE");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FileNotFoundException", result.Error);
        Assert.Contains("Yagu.exe", result.Error);
    }

    [Theory]
    [InlineData("ia64")]
    [InlineData("anycpu")]
    public async Task StagedArchitecture_RejectsUnsupportedInstallerArchitecture(string architecture)
    {
        using var fixture = new ArchitectureFixture();
        fixture.WritePayload(0x8664);

        var result = await fixture.RunAsync("Assert-YaguStagedPayloadArchitecture -StagingDir $env:YAGU_TEST_STAGING -Architecture $env:YAGU_TEST_ARCH", architecture);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ParameterBindingValidationException", result.Error);
        Assert.Contains("Architecture", result.Error);
        Assert.DoesNotContain("arch OK:", result.Output);
    }

    [Fact]
    public async Task StagedArchitecture_IgnoresVendorAndOcrArchitectures()
    {
        using var fixture = new ArchitectureFixture();
        fixture.WritePayload(0x014C);
        fixture.WritePe("ocr-worker\\Yagu.OcrWorker.exe", 0x8664);
        fixture.WritePe("vendor\\third-party.exe", 0xAA64);
        File.WriteAllText(Path.Combine(fixture.StagingDir, "vendor", "not-a-pe.dll"), "not a PE");

        var result = await fixture.RunAsync("Assert-YaguStagedPayloadArchitecture -StagingDir $env:YAGU_TEST_STAGING -Architecture x86");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(3, result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("ocr-worker", result.Output);
        Assert.DoesNotContain("vendor", result.Output);
    }

    [Fact]
    public async Task StagedArchitecture_DotSourcingDoesNotEnableStrictMode()
    {
        using var fixture = new ArchitectureFixture();

        var result = await fixture.RunAsync("Remove-Variable YaguUndefinedFixtureVariable -ErrorAction SilentlyContinue; $null = $YaguUndefinedFixtureVariable; [Console]::WriteLine('strict mode remains off')");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal("strict mode remains off", result.Output.Trim());
    }

    private sealed class ArchitectureFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "yagu arch [fixture]'s " + Guid.NewGuid().ToString("N"));

        public string StagingDir => Path.Combine(_root, "installer", "staging");

        public static string VerifyImageClosed(string command)
            => "try { " + command + " } finally { "
                + "$exclusive = [System.IO.File]::Open($env:YAGU_TEST_IMAGE, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None); "
                + "$exclusive.Dispose() }";

        public void WritePayload(int machine, bool includeIndexWorker = true)
        {
            WritePe("Yagu.exe", machine);
            WritePe("mcp-server\\Yagu.McpServer.exe", machine);
            if (includeIndexWorker)
                WritePe("index-worker\\Yagu.IndexWorker.exe", machine);
        }

        public string WritePe(string relativePath, int machine, int peOffset = 0x80)
        {
            string path = Path.Combine(StagingDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0x5A4D);
            stream.Position = 0x3C;
            writer.Write(peOffset);
            stream.Position = peOffset;
            writer.Write(0x00004550u);
            writer.Write((ushort)machine);
            return path;
        }

        public async Task<(int ExitCode, string Output, string Error)> RunAsync(string command, string architecture = "x64", string? imagePath = null)
        {
            Directory.CreateDirectory(StagingDir);
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _root,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.Environment["YAGU_TEST_HELPER"] = Path.Combine(FindRepoRoot(), "scripts", "staged-payload-arch.ps1");
            startInfo.Environment["YAGU_TEST_STAGING"] = StagingDir;
            startInfo.Environment["YAGU_TEST_ARCH"] = architecture;
            startInfo.Environment["YAGU_TEST_IMAGE"] = imagePath ?? Path.Combine(StagingDir, "Yagu.exe");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-OutputFormat");
            startInfo.ArgumentList.Add("Text");
            startInfo.ArgumentList.Add("-Command");
            string script = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); "
                + "$ErrorActionPreference = 'Stop'; "
                + "if ($PSVersionTable.PSVersion.Major -le 5) { $env:PSModulePath = [Environment]::GetEnvironmentVariable('PSModulePath', 'Machine') }; "
                + "try { . $env:YAGU_TEST_HELPER; " + command + "; exit 0 } "
                + "catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
            startInfo.ArgumentList.Add(script);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return (process.ExitCode, await output, await error);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void McpServer_IsAnOptInTask_NotAnUnconditionalPayload()
    {
        // The self-contained MCP server is ~107 MB on disk and is useless to anyone who never wires up
        // an AI client. It must reach disk only when its task is selected: the blanket staging line
        // excludes it, and a second line installs it under the task.
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        Assert.Contains("Name: \"mcpserver\";", inno);
        Assert.Contains("Flags: unchecked", inno);
        Assert.Contains("Excludes: \"\\mcp-server\\*\"", inno);
        Assert.Contains("Source: \"{#StagingDir}\\mcp-server\\*\"; DestDir: \"{app}\\mcp-server\"; Tasks: mcpserver", inno);

        // A helper surviving uninstall would point at a deleted executable and take a port every logon.
        Assert.Contains("[UninstallRun]", inno);
        Assert.Contains("schtasks /Delete /TN \"\"Yagu MCP Server (%USERNAME%)\"\" /F", inno);
    }

    [Fact]
    public void StagedMcpServerPayload_IsCheckedForCompletenessAndFreshness()
    {
        // Runs the REAL guard against real fixtures. Asserting that the script merely mentions these
        // names would pass even if the comparison were inverted or never reached.
        string root = FindRepoRoot();
        string helper = Path.Combine(root, "scripts", "staged-payload-arch.ps1");

        string fixture = Path.Combine(Path.GetTempPath(), "yagu-payload-" + Guid.NewGuid().ToString("N")[..8]);
        string staging = Path.Combine(fixture, "staging");
        string serverDir = Path.Combine(staging, "mcp-server");
        string projectDir = Path.Combine(fixture, "Yagu.McpServer");
        string projectFile = Path.Combine(projectDir, "Yagu.McpServer.csproj");
        string source = Path.Combine(projectDir, "Program.cs");
        string serverExe = Path.Combine(serverDir, "Yagu.McpServer.exe");
        string runtimeConfig = Path.Combine(serverDir, "Yagu.McpServer.runtimeconfig.json");

        static void Age(string path, double days)
            => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));

        try
        {
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(staging, "Yagu.exe"), "app");
            File.WriteAllText(serverExe, "server");
            File.WriteAllText(Path.Combine(serverDir, "Yagu.McpServer.dll"), "server");
            File.WriteAllText(runtimeConfig, "{}");
            File.WriteAllText(projectFile, "<Project />");
            File.WriteAllText(source, "// server source");

            Age(projectFile, 10);
            Age(source, 10);
            Assert.True(RunPayloadGuard(helper, staging, projectDir), "a complete, current payload was rejected.");

            // Incremental build leaves an unchanged server alone while the app relinks every build, so
            // trailing the app is NOT staleness. Comparing the two reported a current payload as stale.
            Age(serverExe, 3);
            Assert.True(
                RunPayloadGuard(helper, staging, projectDir),
                "a server older than the app but newer than its own sources was rejected.");

            // Stale: a compile input changed after the server was written.
            Age(source, 0);
            Assert.False(RunPayloadGuard(helper, staging, projectDir), "a stale MCP server payload was accepted.");

            Age(source, 10);
            File.Delete(runtimeConfig);
            Assert.False(
                RunPayloadGuard(helper, staging, projectDir),
                "an incomplete MCP server payload was accepted.");
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Dot-sources the packaging guard and reports whether it accepted the staging tree.</summary>
    private static bool RunPayloadGuard(string helperPath, string stagingDir, string projectDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            $". '{helperPath}'; try {{ Assert-YaguStagedMcpServerPayload -StagingDir '{stagingDir}' "
                + $"-ProjectDir '{projectDir}'; exit 0 }} catch {{ exit 1 }}");

        using var process = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(process);
        process!.WaitForExit(60_000);
        return process.ExitCode == 0;
    }

    [Fact]
    public void ShutdownRoutine_AlsoStopsTheMcpServerAndItsRestartingTask()
    {
        // The MCP server is NOT a child of Yagu.exe: a client launches it, or the per-user sign-in task
        // does. "/T" therefore does not reach it, and the task restarts it on failure, so ending the task
        // must come first or it races the taskkill and re-locks the files setup is about to overwrite.
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        int procedure = inno.IndexOf("procedure KillYaguProcesses();", StringComparison.Ordinal);
        Assert.InRange(procedure, 0, int.MaxValue);

        int end = inno.IndexOf("end;", procedure, StringComparison.Ordinal);
        string body = inno[procedure..end];

        // Setup runs ELEVATED, so an image-name kill would reach every session: another user's server,
        // or a side-by-side installation's, terminated mid-request. Match on the executable path instead.
        Assert.DoesNotContain("/IM Yagu.McpServer.exe", body);
        Assert.Contains("Get-Process -Name Yagu.McpServer", body);
        Assert.Contains(@"\mcp-server\*", body);
        Assert.Contains("Stop-Process -Force", body);

        Assert.Contains("schtasks /End /TN \"Yagu MCP Server (%USERNAME%)\"", body);
        Assert.True(
            body.IndexOf("schtasks /End", StringComparison.Ordinal)
                < body.IndexOf("Get-Process -Name Yagu.McpServer", StringComparison.Ordinal),
            "the sign-in task must be ended before the server is killed, or it restarts the process mid-install.");
    }

    [Fact]
    public void PascalScriptStringChange_IsCalledAsAStatementOnAVariable()
    {
        // Inno has TWO StringChange functions. The preprocessor one, written {#StringChange(...)}, returns
        // a string. The Pascal Script one is `function StringChange(var S: String; ...): Integer` -- it
        // edits its first argument in place and returns a count, so using it inline is a hard compile
        // error ("Variable Expected") that no other test in this file catches and that breaks the whole
        // release build.
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(inno, @"(?m)^([^\r\n]*?)\bStringChange\s*\("))
        {
            string prefix = match.Groups[1].Value;

            // The preprocessor form and { } comments are not Pascal Script calls.
            if (prefix.Contains("{#", StringComparison.Ordinal) || prefix.TrimStart().StartsWith('{'))
                continue;

            Assert.True(
                prefix.Trim().Length == 0,
                $"Pascal Script StringChange must be its own statement on a variable, but found: {match.Value.Trim()}");
        }

        Assert.Contains("StringChange(AppDirLiteral,", inno);
    }

    [Fact]
    public void McpServerPayload_IsRequiredRatherThanOptionalWhenPackaging()
    {
        // A publish that quietly failed must not be packaged: the app advertises MCP support in its UI,
        // CLI and help, so shipping with no server (or an earlier run's stale one) is a broken release
        // that a green build says nothing about.
        string root = FindRepoRoot();
        string helper = File.ReadAllText(Path.Combine(root, "scripts", "staged-payload-arch.ps1"));
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));

        Assert.Contains("Path = 'mcp-server\\Yagu.McpServer.exe';         Required = $true", helper);

        // A failed child publish must not leave the previous output behind for the copy step to stage.
        Assert.Contains("PropertyName=\"_McpServerPublishExitCode\"", csproj);
        Assert.Contains("<RemoveDir Condition=\"'$(_McpServerPublishExitCode)' != '0'\" Directories=\"$(McpServerOutDir)\" />", csproj);

        // Fatal at publish time, where the payload is produced; a warning is enough for the dev loop.
        int publishTarget = csproj.IndexOf("Name=\"CopyMcpServerToPublish\"", StringComparison.Ordinal);
        Assert.InRange(publishTarget, 0, int.MaxValue);
        Assert.Contains("<Error Condition=\"!Exists('$(McpServerOutDir)Yagu.McpServer.exe')\"", csproj[publishTarget..]);
    }

    [Fact]
    public void InstallerBuild_AssertsStagedPayloadArchitecture()
    {
        // An x64 server staged into the x86 installer would compile, ship, and only fail on a 32-bit
        // machine. Nothing in the compile chain compares the two, so the build script must.
        string root = FindRepoRoot();
        string build = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        string helper = File.ReadAllText(Path.Combine(root, "scripts", "staged-payload-arch.ps1"));

        Assert.Contains("Assert-YaguStagedPayloadArchitecture -StagingDir $stagingDir -Architecture $arch", build);
        Assert.Contains("staged-payload-arch.ps1", build);

        Assert.Contains("mcp-server\\Yagu.McpServer.exe", helper);
        Assert.Contains("0x8664", helper);
        Assert.Contains("0x014C", helper);
        Assert.Contains("0xAA64", helper);
    }

    [Fact]
    public void DeselectingMcpTask_ClearsAnyPreviouslyInstalledServer()
    {
        // Inno only SKIPS unselected files; it never removes ones already on disk. Verified against a
        // probe installer built from these same directives: upgrading an installation that HAD the
        // server while unticking the task left the OLD binaries behind, so a client registered earlier
        // kept launching a stale server that shells out to the newer Yagu.exe and never receives later
        // fixes. The same pass also leaves a puzzling empty mcp-server folder, because
        // "createallsubdirs" creates the directory even when every file under it is excluded.
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        Assert.Contains("procedure RemoveDeselectedMcpServer();", inno);
        Assert.Contains("if WizardIsTaskSelected('mcpserver') then", inno);
        Assert.Contains("DelTree(ServerDir, True, True, True);", inno);

        // It has to actually run, and only after the payload has been laid down.
        Match step = Regex.Match(inno, @"procedure CurStepChanged\(CurStep: TSetupStep\);.*?\bend;", RegexOptions.Singleline);
        Assert.True(step.Success, "CurStepChanged should exist");
        Assert.Contains("ssPostInstall", step.Value);
        Assert.Contains("RemoveDeselectedMcpServer();", step.Value);
    }

    [Fact]
    public void ArchitectureHelper_DoesNotImposeStrictModeOnTheBuildScript()
    {
        // The helper is dot-sourced into build-installer.ps1, so a Set-StrictMode there would apply to
        // the rest of that script -- which was never written or tested under it, and could fail a
        // release build on an unset variable in an unrelated code path.
        string root = FindRepoRoot();
        string helper = File.ReadAllText(Path.Combine(root, "scripts", "staged-payload-arch.ps1"));

        // Match an actual invocation, not the comment that explains why there isn't one.
        Assert.DoesNotMatch(new Regex(@"(?m)^\s*Set-StrictMode\b"), helper);
    }

    [Fact]
    public void WindowsPlatformDependencies_AreCurrentAndAlignedAcrossProjects()
    {
        string root = FindRepoRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));
        string semanticWorker = File.ReadAllText(Path.Combine(root, "src", "Yagu.SemanticWorker", "Yagu.SemanticWorker.csproj"));
        string textControl = File.ReadAllText(Path.Combine(root, "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "TextControlBox.csproj"));
        string installer = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));
        string globalJson = File.ReadAllText(Path.Combine(root, "global.json"));

        Assert.Contains("<PackageReference Include=\"Microsoft.WindowsAppSDK\" Version=\"2.3.1\" />", app);
        Assert.Contains("<PackageReference Include=\"Microsoft.Windows.SDK.BuildTools\" Version=\"10.0.28000.2526\" />", app);
        Assert.Contains("<PackageReference Include=\"Microsoft.Web.WebView2\" Version=\"1.0.4078.44\"", app);
        Assert.Contains("<PackageReference Include=\"Microsoft.Graphics.Win2D\" Version=\"1.4.0\" />", app);
        Assert.Contains("<PackageReference Include=\"Microsoft.WindowsAppSDK.ML\" Version=\"2.1.74\" />", app);
        Assert.Contains("<PackageReference Include=\"System.Diagnostics.PerformanceCounter\" Version=\"10.0.10\" />", app);
        Assert.Contains("<WindowsSdkPackageVersion>10.0.26100.87</WindowsSdkPackageVersion>", app);

        Assert.Contains("<PackageReference Include=\"Microsoft.WindowsAppSDK.ML\" Version=\"2.1.74\" />", semanticWorker);
        Assert.Contains("<WindowsSdkPackageVersion>10.0.26100.87</WindowsSdkPackageVersion>", semanticWorker);
        Assert.Contains("<PackageReference Include=\"Microsoft.WindowsAppSDK\" Version=\"2.3.1\" />", textControl);
        Assert.Contains("<PackageReference Include=\"Microsoft.Windows.SDK.BuildTools\" Version=\"10.0.28000.2526\" />", textControl);
        Assert.Contains("<PackageReference Include=\"Microsoft.Graphics.Win2D\" Version=\"1.4.0\" />", textControl);
        Assert.Contains("<WindowsSdkPackageVersion>10.0.26100.87</WindowsSdkPackageVersion>", textControl);
        Assert.Contains("Installing Windows app runtime (if not installed)...", installer);
        Assert.DoesNotContain("Windows App Runtime 1.8", installer);
        Assert.Contains("\"version\": \"10.0.302\"", globalJson);
    }

    [Fact]
    public void InstallerBuild_StagesWindowsAppRuntimePrerequisite()
    {
        string root = FindRepoRoot();
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));

        Assert.Contains("windows-app-runtime-prereq.ps1", buildInstaller);
        Assert.Contains("Copy-YaguWindowsAppRuntimePrerequisite -ProjectXml $projectXml -RepoRoot $repoRoot -DestinationRoot $stagingDir", buildInstaller);
        Assert.Contains("Installer app version: $version", buildInstaller);
        // The MSIX filename version token is discovered from the package (works for WAR 1.x "1.8" and
        // 2.x "2"), not derived as major.minor from the SDK version.
        Assert.Contains("Microsoft.WindowsAppRuntime.$runtimeToken.msix", File.ReadAllText(Path.Combine(root, "scripts", "windows-app-runtime-prereq.ps1")));
        Assert.Contains("Microsoft.WindowsAppRuntime.DDLM.$runtimeToken.msix", File.ReadAllText(Path.Combine(root, "scripts", "windows-app-runtime-prereq.ps1")));
    }

    [Fact]
    public void App_ShipsGplLicenseAndThirdPartyNoticesInInstallDir()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));

        // The GPLv3 license and the consolidated third-party notices are copied to the app output so
        // every binary distribution carries the required license/attribution texts (GPLv3 conveyance +
        // the MIT/BSD/Apache/LGPL and 7-Zip/unRAR notice requirements).
        Assert.Contains("<Content Include=\"..\\..\\LICENSE\" Link=\"LICENSE\">", csproj);
        Assert.Contains("<Content Include=\"..\\..\\THIRD-PARTY-NOTICES.txt\" Link=\"THIRD-PARTY-NOTICES.txt\">", csproj);
        // The privacy policy ships beside the app too, so the installer's InfoBefore page and the
        // in-app Help can point at <app>\PRIVACY.md.
        Assert.Contains("<Content Include=\"..\\..\\PRIVACY.md\" Link=\"PRIVACY.md\">", csproj);

        // The installer stages the whole publish output, so those files ship as <app>\LICENSE and
        // <app>\THIRD-PARTY-NOTICES.txt.
        Assert.Contains("Copy-Item -Path \"$publishDir\\*\" -Destination $stagingDir -Recurse -Force", buildInstaller);

        // Sanity: the notices file exists and carries the unRAR redistribution statement required by
        // the bundled 7-Zip 7z.dll.
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));
        Assert.Contains("develop a RAR (WinRAR) compatible archiver", notices);
    }

    [Fact]
    public void Installer_ShowsLicenseNoticesAndPrivacyBeforeInstallation()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));
        string help = File.ReadAllText(Path.Combine(root, "HELP.md"));

        // The GPL agreement is followed immediately by a custom third-party notices page. The privacy
        // policy remains an InfoBefore page so all three documents are shown before installation.
        Assert.Contains("#define RepoRoot \"..\"", inno);
        Assert.Contains(@"LicenseFile={#RepoRoot}\LICENSE", inno);
        Assert.Contains(@"InfoBeforeFile={#RepoRoot}\PRIVACY.md", inno);
        Assert.DoesNotContain("InfoAfterFile=", inno);
        Assert.Contains(@"Source: ""{#RepoRoot}\THIRD-PARTY-NOTICES.txt""; Flags: dontcopy noencryption", inno);
        Assert.Contains("ExtractTemporaryFile('THIRD-PARTY-NOTICES.txt');", inno);
        Assert.Contains("LoadStringsFromFile(", inno);
        Assert.Contains("ThirdPartyNoticesPage := CreateCustomPage(", inno);
        Assert.Contains(
            "wpLicense,\n    'Third-party notices',".ReplaceLineEndings(),
            inno);
        Assert.Contains("ThirdPartyNoticesViewer.Lines.Add(NoticesLines[I]);", inno);
        Assert.DoesNotContain("ACCEPTLICENSE", inno);
        Assert.DoesNotContain("ACCEPTLICENSE", help);
        // HELP.md must describe the current page order. The notices used to be an InfoAfterFile page
        // shown post-install; leaving that sentence behind is silent doc drift.
        Assert.DoesNotContain("third-party notices after installation", help);
        Assert.DoesNotContain("MultiTerm installer flow", help);
        Assert.Contains("third-party notices on the page immediately after it", help);
        Assert.Contains("/VERYSILENT /VERBOSELOG", inno);
        Assert.Contains("/VERYSILENT /VERBOSELOG", help);
    }

    [Fact]
    public void PrivacyPolicy_ExistsAndIsSurfacedDuringSetupAndInAppHelp()
    {
        string root = FindRepoRoot();
        string privacy = File.ReadAllText(Path.Combine(root, "PRIVACY.md"));
        string help = File.ReadAllText(Path.Combine(root, "HELP.md"));
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // The canonical policy exists at the repo root and states the core on-device guarantee.
        Assert.Contains("# Yagu — Privacy Policy", privacy);
        Assert.Contains("never leave your PC", privacy);

        // It is shown during installation (InfoBeforeFile) ...
        Assert.Contains(@"InfoBeforeFile={#RepoRoot}\PRIVACY.md", inno);

        // ... and the in-app Help (rendered from HELP.md) carries a Privacy Policy section that points
        // at the shipped PRIVACY.md so the user can read it from Help inside the app.
        Assert.Contains("## Privacy Policy", help);
        Assert.Contains("PRIVACY.md", help);
    }

    [Fact]
    public void IndexWorker_IsPublishedArchMatched_AndShipsInEveryInstallerVariant()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // The content-index worker is PUBLISHED self-contained (carries its own .NET runtime — Yagu.exe is
        // self-contained Native AOT and provides no shared runtime). UNLIKE the OCR / semantic workers it is
        // arch-matched (its native yagu_core.dll is arch-specific), so its RID tracks the app's RuntimeIdentifier.
        Assert.Contains("dotnet publish &quot;$(IndexWorkerProject)&quot; -c $(Configuration) -r $(IndexWorkerRid) --self-contained true", csproj);
        Assert.Contains("<IndexWorkerRid Condition=\"'$(IndexWorkerRid)' == '' And '$(RuntimeIdentifier)' != ''\">$(RuntimeIdentifier)</IndexWorkerRid>", csproj);

        // Copied into the self-contained publish dir (AfterTargets=Publish) so build-installer.ps1's recursive
        // stage carries it into <staging>\index-worker for EVERY architecture / edition.
        Assert.Contains("$(PublishDir)index-worker\\", csproj);
        Assert.Contains("AfterTargets=\"Publish\"", csproj);
        Assert.Contains("Copy-Item -Path \"$publishDir\\*\" -Destination $stagingDir -Recurse -Force", buildInstaller);

        // The recursesubdirs [Files] entry ships every staged subfolder (incl. index-worker) as <app>\...,
        // so all four installer variants (x64/x86/arm64/offline) contain a runnable worker with no ISS change.
        // Only the opt-in MCP server is excluded; excluding anything else here would silently stop shipping
        // a worker the app needs, and the app would fail at runtime rather than at build time.
        Assert.Contains("Source: \"{#StagingDir}\\*\"; DestDir: \"{app}\"; Excludes: \"\\mcp-server\\*\"; Flags: ignoreversion recursesubdirs createallsubdirs", inno);

        Match blanket = Regex.Match(inno, @"Source: ""\{#StagingDir\}\\\*"".*?Excludes: ""(?<excludes>[^""]*)""");
        Assert.True(blanket.Success, "the blanket staging line was not found.");
        Assert.Equal("\\mcp-server\\*", blanket.Groups["excludes"].Value);
    }

    [Fact]
    public void HelpHtml_IsPublished_SoTheInAppHelpWindowWorksInInstalledBuilds()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));
        string helpWindow = File.ReadAllText(Path.Combine(root, "src", "Yagu", "UI", "Windows", "Help", "HelpWindow.xaml.cs"));

        // The AfterTargets=Build copy only lands HELP.html in $(OutDir); `dotnet publish` (what
        // build-installer.ps1 stages from) shipped without it, so Help said "file was not found".
        Assert.Contains("<Target Name=\"AddHelpHtmlToPublish\"", csproj);
        Assert.Contains("BeforeTargets=\"ComputeResolvedFilesToPublishList\"", csproj);
        Assert.Contains("<ResolvedFileToPublish Include=\"$(MSBuildThisFileDirectory)HELP.html\">", csproj);
        Assert.Contains("<RelativePath>HELP.html</RelativePath>", csproj);

        // Help resolves the file beside the exe, which is where the publish/staging copy lands.
        Assert.Contains("The generated help file was not found", helpWindow);
    }

    [Fact]
    public void OcrWorker_IsPublishedSelfContained_SoItRunsWithoutDotnetInstalled()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));

        // The OCR worker is a separate managed process (PaddleSharp/Tesseract are not Native-AOT safe).
        // It must be PUBLISHED self-contained for win-x64 so it carries its own .NET runtime: Yagu.exe is
        // self-contained Native AOT and provides no shared runtime, so a framework-dependent worker fails
        // with "You must install .NET" on clean/offline machines (image-text OCR silently broke there).
        Assert.Contains("dotnet publish &quot;$(OcrWorkerProject)&quot; -c $(Configuration) -r win-x64 --self-contained true", csproj);
        Assert.Contains("net10.0\\win-x64\\publish\\", csproj);
        Assert.DoesNotContain("dotnet build &quot;$(OcrWorkerProject)&quot;", csproj);
    }

    [Fact]
    public void Installers_RunWindowsAppRuntimePrerequisiteBeforeLaunchOrCopy()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        Assert.Contains("InstallWindowsAppRuntime", inno);
        Assert.Contains("Install-WindowsAppRuntime.ps1", inno);
        Assert.Contains("if not InstallWindowsAppRuntime() then", inno);
        Assert.Contains("Abort;", inno);
    }

    [Fact]
    public void Uninstaller_PromptsToKeepOrDeleteUserSettingsFile()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // Interactive uninstall asks whether to keep the per-user settings file; the prompt targets
        // the exact %APPDATA%\Yagu\settings.json that SettingsService uses.
        Assert.Contains("procedure MaybeRemoveUserSettings();", inno);
        Assert.Contains(@"ExpandConstant('{userappdata}\Yagu\settings.json')", inno);
        Assert.Contains("Do you want to keep your Yagu settings and preferences?", inno);
        Assert.Contains("mbConfirmation, MB_YESNO) = IDNO then", inno);

        // Default is to KEEP: silent uninstalls never prompt/delete, and only an explicit "No"
        // removes the file (folder removed only if it becomes empty, preserving logs/other data).
        Assert.Contains("if UninstallSilent() then", inno);
        Assert.Contains("DeleteFile(SettingsFile);", inno);
        Assert.Contains(@"RemoveDir(ExpandConstant('{userappdata}\Yagu'));", inno);

        // The prompt is wired into the post-uninstall step alongside the registry cleanup.
        Assert.Contains("MaybeRemoveUserSettings();", inno);
    }

    [Fact]
    public void InstallerAndUninstaller_PromptToKeepOrDeleteContentIndexesSafely()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // Setup visibly detects both 32-bit and 64-bit registrations plus saved settings/index data,
        // then presents independent, default-on preservation choices before installation starts.
        Assert.Contains("procedure DetectExistingYaguState();", inno);
        Assert.Contains("DetectRegisteredInstall(HKCU32);", inno);
        Assert.Contains("DetectRegisteredInstall(HKCU64);", inno);
        Assert.Contains("DetectRegisteredInstall(HKLM32);", inno);
        Assert.Contains("DetectRegisteredInstall(HKLM64);", inno);
        Assert.Contains("procedure InitializeWizard();", inno);
        Assert.Contains("Existing Yagu installation or data found", inno);
        Assert.Contains("function CountRecognizedIndexScopes(const IndexRoot: String): Integer;", inno);
        Assert.Contains("\\b Recognized content indexes: ", inno);
        Assert.Contains("IntToStr(ExistingIndexScopeCount)", inno);
        Assert.Contains("Warning: clearing a Keep option permanently deletes that data.", inno);

        // The summary uses native rich text for meaningful emphasis. Preservation choices are fixed
        // below it rather than placed in TInputOptionWizardPage's scrollable options strip.
        Assert.Contains("ExistingDataPage := CreateCustomPage(", inno);
        Assert.Contains("ExistingSummaryViewer := TRichEditViewer.Create(ExistingDataPage);", inno);
        Assert.Contains("ExistingSummaryViewer.UseRichEdit := True;", inno);
        Assert.Contains("ExistingSummaryViewer.RTFText := Rtf;", inno);
        Assert.Contains("\\b\\cf3 Keep is the recommended default.", inno);
        Assert.Contains("\\b\\cf2 Warning: clearing a Keep option permanently deletes that data.", inno);
        Assert.Contains("OptionTop := ExistingDataPage.SurfaceHeight - (OptionCount * ScaleY(22));", inno);
        Assert.Contains("Keep settings and apply supported migrations (recommended)", inno);
        Assert.Contains("Keep content indexes; avoids rebuilding (recommended)", inno);
        Assert.Contains("Result := TNewCheckBox.Create(ExistingDataPage);", inno);
        Assert.Contains("Result.Checked := True;", inno);
        Assert.DoesNotContain("CreateInputOptionPage(", inno);

        Assert.Contains("procedure MaybeRemoveContentIndexes(DuringUninstall: Boolean);", inno);
        Assert.Contains("Do you want to keep your existing Yagu content indexes?", inno);
        Assert.Contains("Building it again can take a long time.", inno);
        Assert.Contains("mbConfirmation, MB_YESNO) = IDNO then", inno);
        Assert.Contains("(DuringUninstall and UninstallSilent())", inno);
        Assert.Contains("((not DuringUninstall) and WizardSilent())", inno);

        // The default per-user location is dedicated to Yagu. A custom root may contain unrelated files,
        // so setup identifies scope directories and never recursively deletes that custom root itself.
        Assert.Contains(@"ExpandConstant('{localappdata}\Yagu\content-index')", inno);
        Assert.Contains("TryReadJsonStringProperty(String(SettingsJson), 'IndexStorageDirectory', ParsedRoot)", inno);
        Assert.Contains("'PreservedIndexStorageDirectory'", inno);
        Assert.Contains("IsRecognizedIndexScope(Candidate)", inno);
        Assert.Contains("DeleteRecognizedIndexData(DefaultRoot, True);", inno);
        Assert.Contains("DeleteRecognizedIndexData(CustomRoot, False);", inno);
        Assert.Contains("if DeleteDedicatedRoot then", inno);
        Assert.Contains("RegWriteStringValue(HKCU, 'Software\\Yagu', 'PreservedIndexStorageDirectory', PreservedCustomIndexRoot);", inno);

        // Interactive uninstall asks before removing files. Install applies the visible page choices only
        // after Install is clicked; silent setup preserves settings and indexes without prompting.
        Assert.Contains("MaybeRemoveContentIndexes(True);", inno);
        Assert.Contains("MaybeRemoveContentIndexes(False);", inno);
        Assert.Contains("procedure MaybeRemoveUserSettingsDuringInstall();", inno);
        Assert.Contains("MaybeRemoveUserSettingsDuringInstall();", inno);
        Assert.DoesNotContain("if not FileExists(ExpandConstant('{app}\\{#MyAppExeName}')) then", inno);
    }

    [Fact]
    public void Installer_KillsRunningYaguProcessesBeforeInstallAndUninstall()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // One helper force-terminates the app PLUS both out-of-process workers so no file is locked
        // while setup writes (install) or deletes (uninstall) them. "/T" also takes the worker subtree.
        Assert.Contains("procedure KillYaguProcesses();", inno);
        Assert.Contains(@"ExpandConstant('{sys}\taskkill.exe')", inno);
        Assert.Contains("'/F /T /IM Yagu.exe'", inno);
        Assert.Contains("'/F /IM Yagu.SemanticWorker.exe'", inno);
        Assert.Contains("'/F /IM Yagu.OcrWorker.exe'", inno);

        // Install path: PrepareToInstall runs before any files are written (and before the Restart
        // Manager scan). Uninstall path: InitializeUninstall runs before any files are removed.
        Assert.Contains("function PrepareToInstall(var NeedsRestart: Boolean): String;", inno);
        Assert.Contains("function IsDecimalProcessId(const Value: String): Boolean;", inno);
        Assert.Contains("(Length(Value) >= 1) and (Length(Value) <= 10)", inno);
        Assert.Contains("(Value[I] < '0') or (Value[I] > '9')", inno);
        Assert.Contains("procedure WaitForLaunchingYagu();", inno);
        Assert.Contains("{param:YAGUWAITPID|}", inno);
        Assert.Contains("if not IsDecimalProcessId(WaitPid) then", inno);
        Assert.Contains("Wait-Process -Id ", inno);
        Assert.Contains("WaitForLaunchingYagu();", inno);
        Assert.Contains("function InitializeUninstall(): Boolean;", inno);

        // The killer is defined before both hooks (Pascal requires top-down definition) and called by each
        // (definition header + one call per hook => at least three occurrences of "KillYaguProcesses();").
        int killerDef = inno.IndexOf("procedure KillYaguProcesses();", System.StringComparison.Ordinal);
        int pidValidatorDef = inno.IndexOf("function IsDecimalProcessId(", System.StringComparison.Ordinal);
        int waiterDef = inno.IndexOf("procedure WaitForLaunchingYagu();", System.StringComparison.Ordinal);
        int prepare = inno.IndexOf("function PrepareToInstall(", System.StringComparison.Ordinal);
        int uninit = inno.IndexOf("function InitializeUninstall(", System.StringComparison.Ordinal);
        Assert.True(killerDef >= 0 && prepare > killerDef && uninit > killerDef,
            "KillYaguProcesses must be defined before the install/uninstall hooks that call it.");
        Assert.True(pidValidatorDef >= 0 && waiterDef > pidValidatorDef,
            "The PID syntax validator must run before the installer constructs its PowerShell wait command.");
        Assert.True(
            inno.IndexOf("WaitForLaunchingYagu();", prepare, System.StringComparison.Ordinal)
                < inno.IndexOf("KillYaguProcesses();", prepare, System.StringComparison.Ordinal),
            "The update installer must wait for graceful Yagu shutdown before its force-close fallback.");
        int occurrences = System.Text.RegularExpressions.Regex.Matches(inno, @"KillYaguProcesses\(\);").Count;
        Assert.True(occurrences >= 3, $"expected KillYaguProcesses called from both hooks, found {occurrences} occurrence(s).");
    }

    [Fact]
    public void Installer_AbortsWhenSmartAppControlIsEnforcing()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // SAC mode is read from the canonical CI policy DWORD; only Enforce (state 1) blocks.
        Assert.Contains("function SmartAppControlEnforced(): Boolean;", inno);
        Assert.Contains(@"SYSTEM\CurrentControlSet\Control\CI\Policy", inno);
        Assert.Contains("VerifiedAndReputablePolicyState", inno);
        Assert.Contains("Result := (State = 1);", inno);

        // The check runs in InitializeSetup and cancels setup before any files are copied.
        Assert.Contains("function InitializeSetup(): Boolean;", inno);
        Assert.Contains("if SmartAppControlEnforced() then", inno);

        // Signed builds are compiled with /DYaguSigned=1 and skip the gate: SAC allows signed apps.
        Assert.Contains("#ifndef YaguSigned", inno);
    }

    [Fact]
    public void CodeSigning_IsOptInAndCoversEveryYaguBinaryPlusTheInstaller()
    {
        string root = FindRepoRoot();
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        string signing = File.ReadAllText(Path.Combine(root, "scripts", "code-signing.ps1"));
        string buildAll = File.ReadAllText(Path.Combine(root, "build-all-installers.ps1"));

        // Opt-in: no thumbprint means the build stays unsigned and never touches signtool.
        Assert.Contains("[string]$SignCertThumbprint", buildInstaller);
        Assert.Contains("$signBuild = -not [string]::IsNullOrWhiteSpace($SignCertThumbprint)", buildInstaller);
        Assert.Contains("if ($signBuild) {", buildInstaller);

        // Signing is all-or-nothing: IsWorkerTrustedForHost rejects workers whose publisher differs
        // from a signed host, and IsInstallerTrustedForHostPublisher rejects updates whose publisher
        // differs from the running build. Signing only Yagu.exe would break OCR, semantic search, and
        // in-app updates.
        Assert.Contains("$_.Name -like 'Yagu*' -or $_.Name -eq 'yagu_core.dll'", signing);
        Assert.Contains("Get-YaguSignableStagedFile -StagingDir $stagingDir", buildInstaller);
        Assert.Contains("Invoke-YaguAuthenticodeSign -Path @($installerExe)", buildInstaller);

        // Staged binaries must be signed BEFORE Inno compresses them into the setup EXE.
        int signStaged = buildInstaller.IndexOf("Get-YaguSignableStagedFile", StringComparison.Ordinal);
        int compile = buildInstaller.IndexOf("& $InnoSetupPath @isccArgs", StringComparison.Ordinal);
        Assert.True(signStaged >= 0 && compile > signStaged, "Staged binaries must be signed before the Inno compile step.");

        // One signtool invocation (hardware tokens prompt per invocation), SHA-256 + RFC 3161
        // timestamp, and a verify pass so a signed-but-untrusted build fails here instead of at runtime.
        Assert.Contains("'/fd', 'SHA256'", signing);
        Assert.Contains("'/tr', $TimestampUrl", signing);
        Assert.Contains("'/td', 'SHA256'", signing);
        Assert.Contains("$verifyArgs = @('verify', '/pa', '/all', '/q') + $files", signing);
        Assert.Contains("throw \"signtool verify failed", signing);

        // The certificate is resolved before any build work so a missing token fails fast.
        int resolveCert = buildInstaller.IndexOf("Resolve-YaguSigningCertificate", StringComparison.Ordinal);
        int publish = buildInstaller.IndexOf("dotnet publish @publishArgs", StringComparison.Ordinal);
        Assert.True(resolveCert >= 0 && publish > resolveCert, "The signing certificate must be resolved before the publish step.");

        // Signed builds tell the installer script to drop the Smart App Control abort.
        Assert.Contains("$isccArgs += '/DYaguSigned=1'", buildInstaller);

        // The multi-variant release workflow forwards the signing options to every variant.
        Assert.Contains("$params['SignCertThumbprint'] = $SignCertThumbprint", buildAll);
    }

    [Fact]
    public void ReleaseWorkflow_KeepsBothDownloadTablesOnTheSameRelease()
    {
        string root = FindRepoRoot();
        string buildAll = File.ReadAllText(Path.Combine(root, "build-all-installers.ps1"));

        // installer/README.md drifted 24 versions behind because only the root README was
        // refreshed after a build. Both tables are now rewritten by the same run...
        Assert.Contains("Update-ReadmeDownloadTable -ReadmePath (Join-Path $repoRoot 'README.md')", buildAll);
        Assert.Contains(@"Update-ReadmeDownloadTable -ReadmePath (Join-Path $repoRoot 'installer\README.md')", buildAll);

        // ...and the post-build guard must permit the second file, or every release aborts with
        // "Unexpected post-build change(s) will not be committed or pushed".
        Assert.Contains("'installer/README.md'", buildAll);

        // Both tables must advertise the same release; a mismatch means one was left stale.
        var rx = new Regex(@"YaguSetup-([0-9.]+)-x64\.exe");
        string rootVersion = rx.Match(File.ReadAllText(Path.Combine(root, "README.md"))).Groups[1].Value;
        string installerVersion = rx.Match(File.ReadAllText(Path.Combine(root, "installer", "README.md"))).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(rootVersion), "README.md has no x64 download row.");
        Assert.Equal(rootVersion, installerVersion);
    }

    [Fact]
    public void Installer_RequiresAdminSoUpdatesReplaceThePerMachineInstall()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // Yagu installs per-machine under {commonpf}\Yagu (C:\Program Files\Yagu), so an UPDATE must be
        // able to overwrite that location. Requiring admin makes Setup auto-elevate (UAC) on every run,
        // so a re-install/update always lands in the existing per-machine install without the user having
        // to know to "run as administrator". The old `lowest` + `PrivilegesRequiredOverridesAllowed=dialog`
        // model ran the installer NON-elevated by default and silently failed to update Program Files.
        Assert.Contains("PrivilegesRequired=admin", inno);
        Assert.Contains(@"DefaultDirName={autopf}\{#MyAppName}", inno);
        Assert.DoesNotContain("PrivilegesRequired=lowest", inno);
        Assert.DoesNotContain("PrivilegesRequiredOverridesAllowed", inno);
    }

    [Fact]
    public void InnoInstaller_IsArchitectureParameterizedAndSelfContained()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // Per-architecture parametrization: build-installer.ps1 passes /DYaguArch.
        Assert.Contains("#ifndef YaguArch", inno);
        Assert.Contains("#define YaguArch \"x64\"", inno);
        Assert.Contains("OutputBaseFilename=YaguSetup-{#MyAppVersion}-{#YaguArch}", inno);
        Assert.Contains("#if YaguArch == \"arm64\"", inno);
        Assert.Contains("ArchitecturesAllowed=arm64", inno);
        Assert.Contains("ArchitecturesInstallIn64BitMode=arm64", inno);
        Assert.Contains("#elif YaguArch == \"x86\"", inno);
        Assert.Contains("ArchitecturesAllowed=x86compatible", inno);
        Assert.Contains("ArchitecturesAllowed=x64compatible", inno);
        Assert.Contains("ArchitecturesInstallIn64BitMode=x64compatible", inno);

        // Self-contained Native AOT: no .NET runtime check / winget / download fallback.
        Assert.DoesNotContain("DotNet10", inno);
        Assert.DoesNotContain("Microsoft.DotNet.DesktopRuntime", inno);
        Assert.DoesNotContain("EnsureDotNet10RuntimeInstalled", inno);
        Assert.DoesNotContain("winget", inno);
        Assert.DoesNotContain("windowsdesktop-runtime", inno);

        // The Windows App Runtime prerequisite is still installed at post-install.
        Assert.Contains("function InstallWindowsAppRuntime(): Boolean;", inno);
        Assert.Contains("if not InstallWindowsAppRuntime() then", inno);
    }

    [Fact]
    public void BuildInstaller_ProducesOneInstallerPerArchitecture()
    {
        string root = FindRepoRoot();
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));

        // Accepts an architecture selector defaulting to all three.
        Assert.Contains("[ValidateSet('x64', 'x86', 'arm64', 'all')]", buildInstaller);
        Assert.Contains("$architectures = @('x64', 'x86', 'arm64')", buildInstaller);

        // Publishes self-contained per RID and suppresses the recursive installer hook. The publish
        // invocation is built as a splatted argument array (@publishArgs) rather than an inline command.
        Assert.Contains("$publishArgs = @($projectPath, '-c', 'Release', '-r', $rid", buildInstaller);
        Assert.Contains("& dotnet publish @publishArgs", buildInstaller);
        Assert.Contains("--self-contained", buildInstaller);
        Assert.Contains("-p:BuildInstallerOnPublish=false", buildInstaller);

        // Compiles one installer per architecture and keeps the latest per arch. The optional
        // offline (OCR-bundled) edition appends an "-offline" suffix to the output name (and its
        // retention filter), so both names are built from the shared $ocrSuffix token.
        Assert.Contains("/DYaguArch=$arch", buildInstaller);
        Assert.Contains("$ocrSuffix = if ($IncludeOcr) { '-offline' } else { '' }", buildInstaller);
        Assert.Contains("YaguSetup-$version-$arch$ocrSuffix.exe", buildInstaller);
        Assert.Contains("-Filter \"YaguSetup-*-$arch$ocrSuffix.exe\"", buildInstaller);
    }

    [Fact]
    public void OfflineEdition_BundlesVoidtoolsEverythingSetupAndLicense()
    {
        string root = FindRepoRoot();
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        string prereq = File.ReadAllText(Path.Combine(root, "scripts", "everything-prereq.ps1"));
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));
        string license = File.ReadAllText(Path.Combine(root, "installer", "Everything-License.txt"));

        // build-installer.ps1 loads the helper and stages the bundle only for the offline (-IncludeOcr) edition.
        Assert.Contains("scripts\\everything-prereq.ps1", buildInstaller);
        Assert.Contains("Copy-YaguEverythingPrerequisite -RepoRoot $repoRoot -DestinationRoot $stagingDir", buildInstaller);

        // The helper downloads the voidtools setup, stages it under everything-setup, and copies the notice.
        Assert.Contains("function Copy-YaguEverythingPrerequisite", prereq);
        Assert.Contains("https://www.voidtools.com/", prereq);
        Assert.Contains("everything-setup", prereq);
        Assert.Contains("Everything-License.txt", prereq);

        // The bundled setup version must match the version the app resolves and downloads (no drift).
        Assert.Contains($"$script:EverythingVersion = '{Yagu.Services.EverythingAssetPaths.Version}'", prereq);

        // The recursesubdirs [Files] entry ships <staging>\everything-setup; the ISS documents it.
        Assert.Contains("everything-setup", inno);

        // The redistribution notice carries the voidtools copyright + the MIT-style permission notice.
        Assert.Contains("Copyright (C) 2018 David Carpenter", license);
        Assert.Contains("Permission is hereby granted", license);
    }

    [Fact]
    public void Csproj_CrossCompilesRustCoreAndPackagesPerArchitecture()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));

        // RuntimeIdentifier maps to an installer architecture token, and the
        // AfterPublish hook packages exactly that architecture (only when a RID is set).
        Assert.Contains("<YaguInstallerArch Condition=\"'$(YaguInstallerArch)' == '' And '$(RuntimeIdentifier)' == 'win-x64'\">x64</YaguInstallerArch>", csproj);
        Assert.Contains("-SkipBuild -Architecture $(YaguInstallerArch)", csproj);
        Assert.Contains("And '$(YaguInstallerArch)' != ''", csproj);

        // The Rust core is cross-compiled to match the RID via cargo --target.
        Assert.Contains("x86_64-pc-windows-msvc", csproj);
        Assert.Contains("i686-pc-windows-msvc", csproj);
        Assert.Contains("aarch64-pc-windows-msvc", csproj);
        Assert.Contains("--target $(RustTargetTriple)", csproj);
        Assert.Contains("target add $(RustTargetTriple)", csproj);
    }

    [Fact]
    public void Csproj_BarePublishBuildsAllThreeInstallers()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));

        // A bare `dotnet publish` (no -r) lets the SDK auto-infer the host RID, which it
        // signals via UseCurrentRuntimeIdentifier == 'true'. That case fans out to build
        // all installer variants (x64/x86/arm64 + the x64-offline edition) rather than
        // packaging a single architecture.
        Assert.Contains("<Target Name=\"BuildAllInstallersAfterPublish\"", csproj);
        Assert.Contains("'$(UseCurrentRuntimeIdentifier)' == 'true'", csproj);
        Assert.Contains("build-all-installers.ps1&quot;", csproj);

        // The fan-out still honors the opt-out flag used by build-installer.ps1 and
        // the local install/publish scripts so it never recurses.
        Assert.Contains("'$(BuildInstallerOnPublish)' != 'false' And '$(DesignTimeBuild)' != 'true' And '$(UseCurrentRuntimeIdentifier)' == 'true'", csproj);
    }

    [Fact]
    public void RuntimePrerequisiteInstaller_UsesMsixManifestIdentity()
    {
        string root = FindRepoRoot();
        string installScript = File.ReadAllText(Path.Combine(root, "scripts", "install-windows-app-runtime.ps1"));

        Assert.Contains("System.IO.Compression.ZipFile", installScript);
        Assert.Contains("AppxManifest.xml", installScript);
        Assert.DoesNotContain("[string]$RuntimeDir = (Join-Path $PSScriptRoot", installScript);
        Assert.Contains("if ([string]::IsNullOrWhiteSpace($RuntimeDir))", installScript);
        Assert.Contains("Get-AppxPackage -Name $Name -PackageTypeFilter Main,Framework", installScript);
        Assert.Contains("Add-AppxPackage -Path $msixPath -ErrorAction Stop", installScript);
    }

    [Fact]
    public void BuildAllInstallers_SelectsVariantsAndDelegatesToBuildInstaller()
    {
        string root = FindRepoRoot();
        string buildAll = File.ReadAllText(Path.Combine(root, "build-all-installers.ps1"));

        // Selectable variants (one or more, plus 'all') via a validated -Variant list.
        Assert.Contains("[ValidateSet('x64', 'x86', 'arm64', 'x64-offline', 'all')]", buildAll);
        Assert.Contains("[string[]]$Variant = @('all')", buildAll);

        // Only x64-offline bundles OCR (the native PaddleOCR runtime is win-x64 only, so there is no
        // x86-offline / arm64-offline); every variant delegates to build-installer.ps1 instead of duplicating it.
        Assert.Contains("'x64-offline' = @{ Architecture = 'x64'", buildAll);
        Assert.Contains("if ($spec.IncludeOcr) { $params['IncludeOcr'] = $true }", buildAll);
        Assert.Contains("& $buildInstaller @params", buildAll);
    }

    [Fact]
    public void InstallerPush_DelegatesToCanonicalReviewedReleaseWorkflow()
    {
        string root = FindRepoRoot();
        string buildAll = File.ReadAllText(Path.Combine(root, "build-all-installers.ps1"));
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        string gitHelper = File.ReadAllText(Path.Combine(root, "scripts", "installer-git-commits.ps1"));

        Assert.Contains("Invoke-YaguFocusedPendingCommits -RepoRoot $repoRoot -CopilotExecutable $copilotCli", buildAll);
        Assert.Contains("Invoke-YaguInstallerReleaseCommit -RepoRoot $repoRoot", buildAll);
        Assert.DoesNotContain("& git -C $repoRoot add -A", buildAll);

        Assert.True(
            buildAll.IndexOf("Invoke-YaguFocusedPendingCommits", StringComparison.Ordinal)
                < buildAll.IndexOf("$versionFile =", StringComparison.Ordinal),
            "Pending changes must be organized before the release version is incremented.");
        Assert.Contains("$canonicalReleaseScript = Join-Path $repoRoot 'build-all-installers.ps1'", buildInstaller);
        Assert.Contains("& $canonicalReleaseScript @releaseParams", buildInstaller);
        Assert.Contains("if ($SkipBuild) { $releaseParams['SkipBuild'] = $true }", buildInstaller);
        Assert.Contains("if ($SkipVersionIncrement) { $releaseParams['KeepVersion'] = $true }", buildInstaller);
        Assert.Contains("if ($SkipRelease) { $releaseParams['SkipRelease'] = $true }", buildInstaller);
        Assert.Contains("if (-not [string]::IsNullOrWhiteSpace($CopilotPath)) { $releaseParams['CopilotPath'] = $CopilotPath }", buildInstaller);
        Assert.Contains("@('x64-offline')", buildInstaller);
        Assert.True(
            buildInstaller.IndexOf("if ($Push)", StringComparison.Ordinal)
                < buildInstaller.IndexOf("$prereqHelper =", StringComparison.Ordinal),
            "Single-installer publishing must delegate before loading build prerequisites or mutating files.");
        Assert.DoesNotContain("Invoke-YaguFocusedPendingCommits", buildInstaller);
        Assert.DoesNotContain("Invoke-YaguInstallerReleaseCommit", buildInstaller);
        Assert.DoesNotContain("gh release", buildInstaller);
        Assert.DoesNotContain("--generate-notes", buildInstaller);

        Assert.Contains("function ConvertFrom-YaguCopilotCommitPlan", gitHelper);
        Assert.Contains("function Assert-YaguAtomicCommitPlan", gitHelper);
        Assert.Contains("function New-YaguCopilotAtomicCommitPlan", gitHelper);
        Assert.Contains("function Test-YaguAtomicCommitStaging", gitHelper);
        Assert.Contains("function Invoke-YaguAtomicCommitPlan", gitHelper);
        Assert.Contains("UNTRACKED FILE PREVIEW (READ-ONLY FILE READS, BOUNDED)", gitHelper);
        Assert.Contains("-C $RepoRoot -p $prompt --silent --no-color", gitHelper);
        Assert.Contains("--no-custom-instructions --no-ask-user --disable-builtin-mcps --allow-all-tools", gitHelper);
        Assert.Contains("--deny-tool shell --deny-tool write", gitHelper);
        Assert.DoesNotContain("--prompt-file", gitHelper);
        Assert.DoesNotContain(" --no-ask ", gitHelper);
        Assert.Contains("Apply this whole-file commit plan? [yes/abort]", gitHelper);
        Assert.Contains("if ($choice -cne 'yes')", gitHelper);
        Assert.Contains("$env:GIT_INDEX_FILE = $tempIndex", gitHelper);
        Assert.Contains("<# DEFERRED: Interactive hunk workflow retained for possible future restoration.", gitHelper);
        Assert.DoesNotContain("Interactive hunk selection:", gitHelper);
        Assert.Contains("diff --name-only --diff-filter=U", gitHelper);
        Assert.Contains("diff --name-status --find-renames", gitHelper);
        Assert.Contains("Focused commits are not allowed from a detached HEAD", gitHelper);
        Assert.Contains("'MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-merge', 'rebase-apply'", gitHelper);
        Assert.Contains("[Console]::IsInputRedirected", gitHelper);
        Assert.Contains("Unexpected post-build change(s) will not be committed or pushed", gitHelper);
        // Post-build source edits are recoverable: set them aside (never commit them into the release,
        // because they are not in the built binaries) so the run finishes, then restore them.
        Assert.Contains("function Suspend-YaguUnexpectedPostBuildChanges", gitHelper);
        Assert.Contains("function Restore-YaguSetAsideChanges", gitHelper);
        Assert.Contains("if ([Console]::IsInputRedirected) { throw $failure }", gitHelper);
        Assert.Contains("Set these changes aside and finish the release? [setaside/abort]", gitHelper);
        Assert.Contains("if ($choice -cne 'setaside') { throw $failure }", gitHelper);
        Assert.Contains("stash push --include-untracked --message $label -- @unexpected", gitHelper);
        Assert.Contains("$setAsideChanges = Suspend-YaguUnexpectedPostBuildChanges -RepoRoot $repoRoot -AllowedPaths $releaseAllowedPaths", buildAll);
        Assert.Contains("Restore-YaguSetAsideChanges -RepoRoot $repoRoot -SetAside $setAsideChanges", buildAll);
        Assert.True(
            buildAll.IndexOf("$setAsideChanges = Suspend-YaguUnexpectedPostBuildChanges", StringComparison.Ordinal)
                < buildAll.IndexOf("Invoke-YaguInstallerReleaseCommit -RepoRoot $repoRoot", StringComparison.Ordinal),
            "Stray post-build changes must be resolved before the release commit runs its guard.");
        Assert.Contains("& git --no-pager -C $RepoRoot add -A -- @releaseChanges", gitHelper);
        Assert.Contains("commit --only -m $Message -- @releaseChanges", gitHelper);
        Assert.Contains("'README.md'", buildAll);

        // WhatIf exits before release-tool preflight so dry-runs never require gh/copilot.
        Assert.True(
            buildAll.IndexOf("if ($WhatIfPreference)", StringComparison.Ordinal)
                < buildAll.IndexOf("if ($Push -and -not $SkipRelease)", StringComparison.Ordinal),
            "WhatIf must return before GitHub/Copilot preflight checks.");

        // Push release preflight now requires both gh and copilot up front.
        Assert.Contains("[string]$CopilotPath", buildAll);
        Assert.Contains("Resolve-CopilotCliPath", buildAll);
        Assert.Contains("Assert-CopilotCliAvailable", buildAll);
        Assert.Contains("& $CopilotCli --version *> $null", buildAll);

        // Release notes are prepared before git push so failures block mutation.
        Assert.True(
            buildAll.IndexOf("$preparedReleaseNotes = Add-ReleaseCompareLink", StringComparison.Ordinal)
                < buildAll.IndexOf("Write-Host \"Pushing (git push)...\"", StringComparison.Ordinal),
            "Prepared release notes must be finalized before git push.");
    }

    [Fact]
    public void InstallerRelease_AlwaysAddsFullChangelogCompareLink()
    {
        string root = FindRepoRoot();
        string buildAll = File.ReadAllText(Path.Combine(root, "build-all-installers.ps1"));

        Assert.Contains("function Add-ReleaseCompareLink", buildAll);
        Assert.Contains("## Full changelog", buildAll);
        Assert.Contains("[Compare $range]($compareUrl)", buildAll);
        Assert.Contains("https://github.com/$RepositorySlug/compare/$range", buildAll);
        Assert.Contains("function Get-PreviousGitHubReleaseTag", buildAll);
        Assert.Contains("Where-Object { -not $_.isDraft -and $_.tagName -ne $CurrentTag }", buildAll);
        // The notes baseline is the last PUBLISHED release, so commits left behind by an interrupted run
        // (which bumped and tagged nothing) are still described by the next release.
        Assert.Contains("Release notes cover every change from $notesBaseline (last published release) to $headSha.", buildAll);

        // Release notes are Copilot-generated from bounded local context, normalized, then deterministically augmented.
        Assert.Contains("function Get-ReleaseChangeContext", buildAll);
        Assert.Contains("git --no-pager -C $RepoRoot log --no-merges '--pretty=format:%h|%s|%b' $range", buildAll);
        Assert.Contains("'diff', '--no-color', '--minimal', $range, '--'", buildAll);
        Assert.Contains("src/Yagu/HELP.html", buildAll);
        Assert.Contains("src/Yagu/Properties/AppInfo.g.cs", buildAll);
        Assert.Contains("src/Yagu/Properties/build-version.txt", buildAll);
        Assert.Contains("function Normalize-CopilotReleaseNotes", buildAll);
        Assert.Contains("Copilot release notes must start with '## What's changed'.", buildAll);
        Assert.Contains("function New-CopilotReleaseNotes", buildAll);
        Assert.Contains("--deny-tool', 'shell'", buildAll);
        Assert.Contains("--deny-tool', 'write'", buildAll);
        Assert.Contains("--no-custom-instructions", buildAll);
        Assert.Contains("--no-ask-user", buildAll);
        Assert.Contains("'-p', $prompt", buildAll);
        Assert.Contains("yagu-release-context-", buildAll);
        Assert.Contains("& $CopilotCli @args > $stdoutPath 2> $stderrPath", buildAll);
        Assert.Contains("ReadAllText($stdoutPath).Trim()", buildAll);
        Assert.Contains("Remove-Item -LiteralPath $contextPath, $stdoutPath, $stderrPath", buildAll);
        Assert.DoesNotContain("--prompt-file", buildAll);
        Assert.Contains("function Add-DeterministicReleaseSections", buildAll);
        Assert.Contains("## Assets", buildAll);
        Assert.Contains("## Validation", buildAll);
        Assert.Contains("## Installation", buildAll);
        Assert.Contains("SHA-256", buildAll);
        Assert.Contains("freshness-checked and size-checked", buildAll);

        // Existing releases are refreshed with prepared notes; new releases are created with the same prepared notes.
        Assert.Contains("--notes-file", buildAll);
        Assert.Contains("if ($releaseExists)", buildAll);
        Assert.Contains("Get-RemoteReleaseTagTarget -RepoRoot $repoRoot -Tag $tag", buildAll);
        Assert.Contains("release upload $tag @($releaseAssets.FullName) --clobber", buildAll);
        Assert.Contains("Assert-GitHubReleaseMatchesBuild", buildAll);
        Assert.Contains("release notes are missing required heading", buildAll);

        // GitHub generated-notes API must not be used.
        Assert.DoesNotContain("releases/generate-notes", buildAll);
        Assert.DoesNotContain("Get-GitHubGeneratedReleaseNotes", buildAll);

        // Full changelog is appended exactly once by Add-ReleaseCompareLink.
        Assert.Contains("if ($notesWithLink.Contains($compareUrl)) { return $notesWithLink }", buildAll);
    }

    [Fact]
    public void InstallerReleaseScripts_UseExplicitGitNoPagerEverywhere()
    {
        string root = FindRepoRoot();
        string buildAll = File.ReadAllText(Path.Combine(root, "build-all-installers.ps1"));
        string helper = File.ReadAllText(Path.Combine(root, "scripts", "installer-git-commits.ps1"));

        string stripDeferred = @"<#[\s\S]*?#>";
        string buildAllActive = System.Text.RegularExpressions.Regex.Replace(buildAll, stripDeferred, string.Empty);
        string helperActive = System.Text.RegularExpressions.Regex.Replace(helper, stripDeferred, string.Empty);

        Assert.DoesNotMatch(@"(?m)(?<![#\w-])&?\s*git\s+-C\s+", buildAllActive);
        Assert.DoesNotMatch(@"(?m)(?<![#\w-])&?\s*git\s+-C\s+", helperActive);
        Assert.DoesNotMatch(@"(?m)&\s*git\s+@(?![^\r\n]*--no-pager)", buildAllActive);
        Assert.DoesNotMatch(@"(?m)&\s*git\s+@(?![^\r\n]*--no-pager)", helperActive);

        Assert.Contains("git --no-pager -C $RepoRoot diff --stat", buildAll);
        Assert.Contains("git --no-pager -C $repoRoot push", buildAll);
        Assert.Contains("git --no-pager -C $RepoRoot log", buildAll);
        Assert.Contains("git --no-pager -C $RepoRoot commit", helper);
        Assert.Contains("git --no-pager -C $repoRoot push", buildAll);
    }

    [Fact]
    public void Installer_DefaultsDesktopIconAndSystemPathTasksToSelected()
    {
        string root = FindRepoRoot();
        string installerPath = Path.Combine(root, "installer", "yagu-installer.iss");
        string inno = File.ReadAllText(installerPath);
        string desktopIconTask = File.ReadLines(installerPath)
            .Single(line => line.StartsWith(@"Name: ""desktopicon"";", StringComparison.Ordinal));
        string addToPathTask = File.ReadLines(installerPath)
            .Single(line => line.StartsWith(@"Name: ""addtopath"";", StringComparison.Ordinal));

        Assert.Contains(@"{cm:CreateDesktopIcon}", desktopIconTask);
        Assert.DoesNotContain("Flags: unchecked", desktopIconTask);
        Assert.Contains(@"Add Yagu to the system PATH (run 'yagu' from any terminal)", addToPathTask);
        Assert.DoesNotContain("Flags: unchecked", addToPathTask);

        // Editing the system Path env var requires broadcasting WM_SETTINGCHANGE so open apps see it.
        Assert.Contains("ChangesEnvironment=yes", inno);

        // The append is gated on the task AND a duplicate guard (NeedsAddPath), writing the system
        // (HKLM) Path as REG_EXPAND_SZ.
        Assert.Contains(@"Root: HKLM; Subkey: ""{#EnvironmentKey}""; ValueType: expandsz; ValueName: ""Path""; ValueData: ""{olddata};{app}""; Tasks: addtopath; Check: NeedsAddPath('{app}')", inno);
        Assert.Contains("function NeedsAddPath(Param: String): Boolean;", inno);

        // Uninstall removes the entry it added (an appended registry value cannot be reversed declaratively).
        Assert.Contains("procedure RemoveAppFromSystemPath();", inno);
        Assert.Contains("RegWriteExpandStringValue(HKLM, '{#EnvironmentKey}', 'Path', NewPath);", inno);
        Assert.Contains("RemoveAppFromSystemPath();", inno);
    }

    [Fact]
    public void Installer_UsesDarkBrandedWizardWithGeneratedArtwork()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        Assert.Contains("WizardStyle=modern dark includetitlebar hidebevels", inno);
        Assert.Contains(@"WizardImageFile=assets\wizard-dark.png", inno);
        Assert.Contains(@"WizardSmallImageFile=assets\wizard-small-dark.png", inno);
        // The tall panel only appears on the welcome page, so it must stay enabled.
        Assert.Contains("DisableWelcomePage=no", inno);
        Assert.Contains("WizardForm.WelcomeLabel1.Caption := 'Yagu'", inno);

        // Dark surfaces make the classic dark-blue/red/green RTF tints unreadable.
        Assert.Contains(@"'{\colortbl ;\red127\green196\blue255;\red255\green138\blue128;\red126\green231\blue135;}'", inno);

        foreach (string asset in new[] { "wizard-dark.png", "wizard-small-dark.png" })
            Assert.True(File.Exists(Path.Combine(root, "installer", "assets", asset)), $"Missing wizard artwork: {asset}");

        Assert.True(
            File.Exists(Path.Combine(root, "scripts", "build-installer-wizard-images.ps1")),
            "The wizard artwork generator must stay alongside the committed PNGs so branding can be regenerated.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}