using Yagu.Services.Mcp;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Editing a user's MCP client configuration. This is the code with the greatest capacity to do damage
/// in the whole feature: these files hold other people's servers, hand-written comments, and settings
/// Yagu has never heard of. Every test here exists to prove that something the user owns survives.
/// </summary>
public class McpClientRegistrarTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "yagu-registrar-" + Guid.NewGuid().ToString("N"));

    private const string Container = "servers";
    private const string ServerName = "yagu";
    private const string Entry = """
        {
          "type": "stdio",
          "command": "C:\\App\\mcp-server\\Yagu.McpServer.exe",
          "args": [
            "--allowed-root",
            "C:\\Projects"
          ]
        }
        """;

    public McpClientRegistrarTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string NewFile(string? content = null)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        if (content is not null)
            File.WriteAllText(path, content);
        return path;
    }

    private static System.Text.Json.JsonDocument Parse(string path)
        => System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(path),
            new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

    // ── Install ──

    [Theory]
    [InlineData("""{ "servers": 5 }""")]
    [InlineData("""{ "servers": "nope" }""")]
    [InlineData("""{ "servers": [] }""")]
    [InlineData("""{ "servers": null }""")]
    public void Install_RefusesWhenTheContainerIsNotAnObject(string content)
    {
        // Treating a wrongly typed container as "absent" would insert a SECOND 'servers' property, which
        // is ambiguous JSON that different clients resolve differently — one of the two silently wins.
        string path = NewFile(content);

        McpRegistrationResult result = McpClientRegistrar.Install(path, Container, ServerName, Entry);

        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Install_LeavesAWronglyTypedContainerCompletelyUntouched()
    {
        string path = NewFile("""{ "servers": 5, "other": { "keep": true } }""");
        string before = File.ReadAllText(path);

        McpClientRegistrar.Install(path, Container, ServerName, Entry);

        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(McpClientRegistrar.IsInstalled(path, Container, ServerName));
    }

    [Fact]
    public void Install_CreatesTheFileWhenItDoesNotExist()
    {
        string path = NewFile();

        var result = McpClientRegistrar.Install(path, Container, ServerName, Entry);

        Assert.Equal(McpRegistrationOutcome.Installed, result.Outcome);
        using var document = Parse(path);
        Assert.Equal(
            @"C:\App\mcp-server\Yagu.McpServer.exe",
            document.RootElement.GetProperty(Container).GetProperty(ServerName).GetProperty("command").GetString());
    }

    [Fact]
    public void Install_KeepsEveryOtherServer()
    {
        string path = NewFile("""
            {
              "servers": {
                "postgres": { "command": "pg-mcp", "args": ["--dsn", "x"] },
                "github": { "command": "gh-mcp" }
              }
            }
            """);

        var result = McpClientRegistrar.Install(path, Container, ServerName, Entry);

        Assert.Equal(McpRegistrationOutcome.Installed, result.Outcome);
        using var document = Parse(path);
        var servers = document.RootElement.GetProperty(Container);
        Assert.Equal("pg-mcp", servers.GetProperty("postgres").GetProperty("command").GetString());
        Assert.Equal("gh-mcp", servers.GetProperty("github").GetProperty("command").GetString());
        Assert.True(servers.TryGetProperty(ServerName, out _));
    }

    [Fact]
    public void Install_KeepsTopLevelPropertiesItDoesNotUnderstand()
    {
        string path = NewFile("""
            {
              "inputs": [ { "id": "token", "type": "promptString" } ],
              "someFutureSetting": { "nested": true },
              "servers": { "github": { "command": "gh-mcp" } }
            }
            """);

        McpClientRegistrar.Install(path, Container, ServerName, Entry);

        using var document = Parse(path);
        Assert.True(document.RootElement.TryGetProperty("inputs", out _));
        Assert.True(document.RootElement.GetProperty("someFutureSetting").GetProperty("nested").GetBoolean());
    }

    [Fact]
    public void Install_PreservesComments_BecauseTheseFilesAreJsonc()
    {
        string path = NewFile("""
            {
              // My servers. Do not reorder!
              "servers": {
                /* the database one */
                "postgres": { "command": "pg-mcp" }
              }
            }
            """);

        McpClientRegistrar.Install(path, Container, ServerName, Entry);

        string text = File.ReadAllText(path);
        Assert.Contains("// My servers. Do not reorder!", text, StringComparison.Ordinal);
        Assert.Contains("/* the database one */", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_AddsTheContainerWhenTheFileHasNone()
    {
        string path = NewFile("""{ "inputs": [] }""");

        var result = McpClientRegistrar.Install(path, Container, ServerName, Entry);

        Assert.Equal(McpRegistrationOutcome.Installed, result.Outcome);
        using var document = Parse(path);
        Assert.True(document.RootElement.TryGetProperty("inputs", out _));
        Assert.True(document.RootElement.GetProperty(Container).TryGetProperty(ServerName, out _));
    }

    [Fact]
    public void Install_IntoAnEmptyContainer_ProducesValidJson()
    {
        string path = NewFile("""{ "servers": {} }""");

        McpClientRegistrar.Install(path, Container, ServerName, Entry);

        using var document = Parse(path);
        Assert.True(document.RootElement.GetProperty(Container).TryGetProperty(ServerName, out _));
    }

    [Fact]
    public void Install_IsIdempotent_AndUpdatesInPlaceOnReinstall()
    {
        string path = NewFile("""{ "servers": { "github": { "command": "gh-mcp" } } }""");

        Assert.Equal(McpRegistrationOutcome.Installed, McpClientRegistrar.Install(path, Container, ServerName, Entry).Outcome);

        const string updated = """{ "type": "stdio", "command": "D:\\Other\\Yagu.McpServer.exe", "args": [] }""";
        Assert.Equal(McpRegistrationOutcome.Updated, McpClientRegistrar.Install(path, Container, ServerName, updated).Outcome);

        using var document = Parse(path);
        var servers = document.RootElement.GetProperty(Container);
        Assert.Equal(@"D:\Other\Yagu.McpServer.exe", servers.GetProperty(ServerName).GetProperty("command").GetString());
        Assert.Equal("gh-mcp", servers.GetProperty("github").GetProperty("command").GetString());

        // Exactly one Yagu entry, not two.
        int count = 0;
        foreach (var property in servers.EnumerateObject())
            if (property.NameEquals(ServerName)) count++;
        Assert.Equal(1, count);
    }

    // ── Remove ──

    [Fact]
    public void Remove_TakesOnlyYagusEntry()
    {
        string path = NewFile("""
            {
              "servers": {
                "postgres": { "command": "pg-mcp" },
                "yagu": { "command": "old-yagu" },
                "github": { "command": "gh-mcp" }
              }
            }
            """);

        var result = McpClientRegistrar.Remove(path, Container, ServerName);

        Assert.Equal(McpRegistrationOutcome.Removed, result.Outcome);
        using var document = Parse(path);
        var servers = document.RootElement.GetProperty(Container);
        Assert.False(servers.TryGetProperty(ServerName, out _));
        Assert.Equal("pg-mcp", servers.GetProperty("postgres").GetProperty("command").GetString());
        Assert.Equal("gh-mcp", servers.GetProperty("github").GetProperty("command").GetString());
    }

    [Fact]
    public void Remove_LeavesValidJsonWhenYaguWasTheLastEntry()
    {
        string path = NewFile("""
            {
              "servers": {
                "postgres": { "command": "pg-mcp" },
                "yagu": { "command": "old-yagu" }
              }
            }
            """);

        McpClientRegistrar.Remove(path, Container, ServerName);

        using var document = Parse(path);
        Assert.Single(document.RootElement.GetProperty(Container).EnumerateObject());
    }

    [Fact]
    public void Remove_LeavesValidJsonWhenYaguWasTheOnlyEntry()
    {
        string path = NewFile("""{ "servers": { "yagu": { "command": "old-yagu" } } }""");

        McpClientRegistrar.Remove(path, Container, ServerName);

        using var document = Parse(path);
        Assert.Empty(document.RootElement.GetProperty(Container).EnumerateObject());
    }

    [Fact]
    public void Remove_ReportsNotPresentRatherThanTouchingTheFile()
    {
        string path = NewFile("""{ "servers": { "github": { "command": "gh-mcp" } } }""");
        string before = File.ReadAllText(path);

        Assert.Equal(McpRegistrationOutcome.NotPresent, McpClientRegistrar.Remove(path, Container, ServerName).Outcome);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Remove_OnAMissingFile_IsNotPresent()
        => Assert.Equal(McpRegistrationOutcome.NotPresent, McpClientRegistrar.Remove(NewFile(), Container, ServerName).Outcome);

    // ── Refusing to damage ──

    [Fact]
    public void MalformedFile_IsLeftExactlyAsItWas()
    {
        // Rewriting a file we cannot parse is how a tool destroys a configuration someone spent an
        // afternoon on. Refuse, and say why.
        const string broken = "{ \"servers\": { \"github\": { \"command\": ";
        string path = NewFile(broken);

        var install = McpClientRegistrar.Install(path, Container, ServerName, Entry);
        var remove = McpClientRegistrar.Remove(path, Container, ServerName);

        Assert.Equal(McpRegistrationOutcome.Malformed, install.Outcome);
        Assert.Equal(McpRegistrationOutcome.Malformed, remove.Outcome);
        Assert.False(install.Succeeded);
        Assert.Equal(broken, File.ReadAllText(path));
    }

    [Fact]
    public void NonObjectRoot_IsRefused()
    {
        string path = NewFile("[1, 2, 3]");

        Assert.Equal(McpRegistrationOutcome.Malformed, McpClientRegistrar.Install(path, Container, ServerName, Entry).Outcome);
        Assert.Equal("[1, 2, 3]", File.ReadAllText(path));
    }

    [Fact]
    public void ConcurrentEdit_IsDetectedAndNothingIsWritten()
    {
        // An editor with the file open can save between our read and our write. Silently discarding
        // that edit would be worse than refusing.
        string path = NewFile("""{ "servers": { "github": { "command": "gh-mcp" } } }""");

        var registrar = typeof(McpClientRegistrar);
        var write = registrar.GetMethod("Write", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(write);

        // Simulate: we read the file a moment ago (stale stamp), then someone else saved it.
        var stale = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = (McpRegistrationResult)write!.Invoke(
            null, [path, "{}", stale, File.ReadAllText(path).Length, McpRegistrationOutcome.Installed])!;

        Assert.Equal(McpRegistrationOutcome.Failed, result.Outcome);
        Assert.Contains("changed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gh-mcp", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void IsInstalled_ReflectsTheFile()
    {
        string path = NewFile("""{ "servers": { "github": { "command": "gh-mcp" } } }""");
        Assert.False(McpClientRegistrar.IsInstalled(path, Container, ServerName));

        McpClientRegistrar.Install(path, Container, ServerName, Entry);
        Assert.True(McpClientRegistrar.IsInstalled(path, Container, ServerName));

        McpClientRegistrar.Remove(path, Container, ServerName);
        Assert.False(McpClientRegistrar.IsInstalled(path, Container, ServerName));
    }

    [Fact]
    public void InstallThenRemove_RestoresTheOriginalMeaning()
    {
        const string original = """
            {
              "inputs": [],
              "servers": {
                "postgres": { "command": "pg-mcp" }
              }
            }
            """;
        string path = NewFile(original);

        McpClientRegistrar.Install(path, Container, ServerName, Entry);
        McpClientRegistrar.Remove(path, Container, ServerName);

        using var after = Parse(path);
        using var before = System.Text.Json.JsonDocument.Parse(original);
        Assert.Equal(
            before.RootElement.GetProperty("servers").GetProperty("postgres").GetProperty("command").GetString(),
            after.RootElement.GetProperty("servers").GetProperty("postgres").GetProperty("command").GetString());
        Assert.False(after.RootElement.GetProperty("servers").TryGetProperty(ServerName, out _));
    }

    // ── Real client shapes ──

    // ── JSONC the splice must not destroy ──

    [Fact]
    public void Install_WithABraceInsideALeadingComment_DoesNotSpliceIntoTheComment()
    {
        // The container is absent, so a new one is inserted at the root brace. Scanning the text for the
        // first '{' finds the one INSIDE the comment and writes the new property into comment text,
        // destroying every server in the file while reporting success.
        string path = NewFile("""
            // yagu { keep this note
            {
              "other": { "type": "stdio", "command": "other.exe" }
            }
            """);

        McpRegistrationResult result = McpClientRegistrar.Install(path, Container, ServerName, Entry);

        Assert.Equal(McpRegistrationOutcome.Installed, result.Outcome);

        using var document = Parse(path);
        Assert.True(document.RootElement.GetProperty(Container).TryGetProperty(ServerName, out _));
        Assert.True(document.RootElement.GetProperty("other").TryGetProperty("command", out _));
        Assert.Contains("// yagu { keep this note", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_WithACommentBetweenTheValueAndItsComma_LeavesValidJson()
    {
        // The comma is what keeps the file valid once our property goes. A comment hiding it leaves an
        // illegal leading comma behind, which disables every other server in the file.
        string path = NewFile("""
            {
              "servers": {
                "yagu": { "type": "stdio", "command": "yagu.exe" } /* keep */,
                "other": { "type": "stdio", "command": "other.exe" }
              }
            }
            """);

        McpRegistrationResult result = McpClientRegistrar.Remove(path, Container, ServerName);

        Assert.Equal(McpRegistrationOutcome.Removed, result.Outcome);

        using var document = Parse(path);
        var servers = document.RootElement.GetProperty(Container);
        Assert.False(servers.TryGetProperty(ServerName, out _));
        Assert.True(servers.GetProperty("other").TryGetProperty("command", out _));
    }

    [Fact]
    public void Install_OverAnArrayValuedEntry_ReplacesTheWholeValue()
    {
        // Only object values were skipped, so an array value recorded its end just past '[' and the old
        // body survived the replacement as loose tokens.
        string path = NewFile("""
            {
              "servers": {
                "yagu": [1, 2, 3],
                "other": { "type": "stdio", "command": "other.exe" }
              }
            }
            """);

        McpRegistrationResult result = McpClientRegistrar.Install(path, Container, ServerName, Entry);

        Assert.Equal(McpRegistrationOutcome.Updated, result.Outcome);

        using var document = Parse(path);
        var servers = document.RootElement.GetProperty(Container);
        Assert.Equal("stdio", servers.GetProperty(ServerName).GetProperty("type").GetString());
        Assert.True(servers.GetProperty("other").TryGetProperty("command", out _));
    }

    [Fact]
    public void Install_IntoAContainerHoldingOnlyAComment_StaysValid()
    {
        string path = NewFile("""
            {
              "servers": {
                // nothing here yet
              }
            }
            """);

        McpClientRegistrar.Install(path, Container, ServerName, Entry);

        using var document = Parse(path);
        Assert.True(document.RootElement.GetProperty(Container).TryGetProperty(ServerName, out _));
    }

    [Fact]
    public void EveryEditedFileStillParses_AcrossAwkwardButValidJsonc()
    {
        // The backstop for the whole text-splicing approach: whatever the offsets do, an edit that would
        // not parse has to be refused rather than written.
        string[] documents =
        [
            """{ /* lead { brace */ "servers": { "other": { "command": "o" } } }""",
            """{ "servers": { "other": { "command": "o" } /* tail */ } }""",
            """{ "servers": { "yagu": { "command": "old" } /* mid */, "other": { "command": "o" } } }""",
            """{ "servers": { "yagu": [1, 2] } }""",
            """{ "servers": {}, "inputs": [ { "id": "other-key" } ] }""",
        ];

        foreach (string document in documents)
        {
            string install = NewFile(document);
            if (McpClientRegistrar.Install(install, Container, ServerName, Entry).Succeeded)
                Parse(install).Dispose();

            string remove = NewFile(document);
            if (McpClientRegistrar.Remove(remove, Container, ServerName, McpConfigSnippetBuilder.TokenInputId).Succeeded)
                Parse(remove).Dispose();
        }
    }

    // ── Loopback HTTP entries and their token input ──

    private static readonly Yagu.Services.AppSettings HttpSettings = new() { McpHttpPort = 8377 };

    private static string HttpEntry() => McpConfigSnippetBuilder.BuildHttpServerEntry(HttpSettings);
    private static string TokenInput() => McpConfigSnippetBuilder.BuildTokenInput();

    private static McpRegistrationResult InstallHttp(string path) =>
        McpClientRegistrar.Install(
            path, Container, ServerName, HttpEntry(),
            McpConfigSnippetBuilder.TokenInputId, TokenInput());

    [Fact]
    public void HttpInstall_ReferencesTheTokenInsteadOfEmbeddingIt()
    {
        // The whole point of the input variable. A literal token here would be written into a file that
        // Settings Sync replicates to every machine the user signs in on.
        string path = NewFile();

        InstallHttp(path);

        string text = File.ReadAllText(path);
        Assert.Contains("${input:" + McpConfigSnippetBuilder.TokenInputId + "}", text, StringComparison.Ordinal);

        using var document = Parse(path);
        var server = document.RootElement.GetProperty(Container).GetProperty(ServerName);
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:8377", server.GetProperty("url").GetString());
        Assert.Equal(
            "Bearer ${input:" + McpConfigSnippetBuilder.TokenInputId + "}",
            server.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public void HttpInstall_WritesTheInputDefinitionTheEntryReferences()
    {
        // A ${input:} reference with no matching definition is a broken entry, so the two must land
        // together or not at all.
        string path = NewFile();

        InstallHttp(path);

        using var document = Parse(path);
        var inputs = document.RootElement.GetProperty(McpConfigSnippetBuilder.InputsPropertyName);
        Assert.Equal(1, inputs.GetArrayLength());

        var input = inputs[0];
        Assert.Equal(McpConfigSnippetBuilder.TokenInputId, input.GetProperty("id").GetString());
        Assert.Equal("promptString", input.GetProperty("type").GetString());
        Assert.True(input.GetProperty("password").GetBoolean());
    }

    [Fact]
    public void HttpInstall_KeepsInputsBelongingToOtherServers()
    {
        string path = NewFile("""
            {
              "servers": {
                "other": { "type": "stdio", "command": "other.exe" }
              },
              "inputs": [
                { "type": "promptString", "id": "other-key", "description": "Other", "password": true }
              ]
            }
            """);

        InstallHttp(path);

        using var document = Parse(path);
        var inputs = document.RootElement.GetProperty(McpConfigSnippetBuilder.InputsPropertyName);
        Assert.Equal(2, inputs.GetArrayLength());

        var ids = inputs.EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Contains("other-key", ids);
        Assert.Contains(McpConfigSnippetBuilder.TokenInputId, ids);

        Assert.True(document.RootElement.GetProperty(Container).TryGetProperty("other", out _));
    }

    [Fact]
    public void HttpInstall_IsIdempotentAndNeverDuplicatesTheInput()
    {
        string path = NewFile();

        InstallHttp(path);
        InstallHttp(path);
        InstallHttp(path);

        using var document = Parse(path);
        Assert.Equal(1, document.RootElement.GetProperty(McpConfigSnippetBuilder.InputsPropertyName).GetArrayLength());
    }

    [Fact]
    public void HttpInstall_AddsToAnExistingEmptyInputsArray()
    {
        string path = NewFile("""
            {
              "servers": {},
              "inputs": []
            }
            """);

        InstallHttp(path);

        using var document = Parse(path);
        Assert.Equal(1, document.RootElement.GetProperty(McpConfigSnippetBuilder.InputsPropertyName).GetArrayLength());
    }

    [Fact]
    public void Remove_TakesTheTokenInputWithTheServerEntry()
    {
        // Leaving the definition behind would keep prompting for a server that no longer exists.
        string path = NewFile();
        InstallHttp(path);

        McpRegistrationResult result = McpClientRegistrar.Remove(
            path, Container, ServerName, McpConfigSnippetBuilder.TokenInputId);

        Assert.Equal(McpRegistrationOutcome.Removed, result.Outcome);

        using var document = Parse(path);
        Assert.False(document.RootElement.GetProperty(Container).TryGetProperty(ServerName, out _));
        Assert.Empty(document.RootElement.GetProperty(McpConfigSnippetBuilder.InputsPropertyName).EnumerateArray());
    }

    [Fact]
    public void Remove_LeavesOtherInputsAlone()
    {
        string path = NewFile();
        InstallHttp(path);

        // A second, unrelated input added by the user afterwards.
        string text = File.ReadAllText(path).Replace(
            "\"inputs\": [",
            "\"inputs\": [\n    { \"type\": \"promptString\", \"id\": \"other-key\", \"description\": \"Other\" },",
            StringComparison.Ordinal);
        File.WriteAllText(path, text);

        McpClientRegistrar.Remove(path, Container, ServerName, McpConfigSnippetBuilder.TokenInputId);

        using var document = Parse(path);
        var ids = document.RootElement.GetProperty(McpConfigSnippetBuilder.InputsPropertyName)
            .EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();

        Assert.Equal(["other-key"], ids);
    }

    [Theory]
    [InlineData("""{ "servers": {}, "inputs": 5 }""")]
    [InlineData("""{ "servers": {}, "inputs": "nope" }""")]
    [InlineData("""{ "servers": {}, "inputs": { "id": "x" } }""")]
    public void HttpInstall_RefusesWhenInputsIsNotAnArray(string content)
    {
        string path = NewFile(content);

        McpRegistrationResult result = InstallHttp(path);

        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void HttpInstall_PreservesCommentsAndUnknownProperties()
    {
        string path = NewFile("""
            {
              // keep me
              "servers": {
                "other": { "type": "stdio", "command": "other.exe" }
              },
              "unknownTopLevel": { "keep": true }
            }
            """);

        InstallHttp(path);

        string text = File.ReadAllText(path);
        Assert.Contains("// keep me", text, StringComparison.Ordinal);

        using var document = Parse(path);
        Assert.True(document.RootElement.GetProperty("unknownTopLevel").GetProperty("keep").GetBoolean());
        Assert.True(document.RootElement.GetProperty(Container).TryGetProperty("other", out _));
    }

    [Fact]
    public void HttpEntry_IsOfferedOnlyToClientsThatExpandInputVariables()
    {
        // Copilot CLI's file is read natively by the Agent Host, which does not expand ${input:} and
        // would send the literal text as the token; Claude Desktop has no HTTP entry shape at all.
        Assert.True(McpConfigSnippetBuilder.SupportsHttpEntry(McpClientKind.VsCode));
        Assert.True(McpConfigSnippetBuilder.SupportsHttpEntry(McpClientKind.VisualStudio));
        Assert.False(McpConfigSnippetBuilder.SupportsHttpEntry(McpClientKind.CopilotCli));
        Assert.False(McpConfigSnippetBuilder.SupportsHttpEntry(McpClientKind.ClaudeDesktop));
    }

    [Fact]
    public void HttpSnippet_CarriesBothTheEntryAndItsInput()
    {
        string snippet = McpConfigSnippetBuilder.BuildHttpClientConfig(McpClientKind.VsCode, HttpSettings);

        using var document = System.Text.Json.JsonDocument.Parse(snippet);
        Assert.Equal("http", document.RootElement.GetProperty("servers").GetProperty(ServerName).GetProperty("type").GetString());
        Assert.Equal(
            McpConfigSnippetBuilder.TokenInputId,
            document.RootElement.GetProperty(McpConfigSnippetBuilder.InputsPropertyName)[0].GetProperty("id").GetString());
    }

    [Fact]
    public void HttpInstall_ProducesReadableIndentedJson()
    {
        // This edits a file the user opens and hand-edits. Valid-but-mangled output is a real failure
        // mode for text splicing, and a JSON round-trip alone would not catch it.
        string path = NewFile("""
            {
              "servers": {
                "other": { "type": "stdio", "command": "other.exe" }
              }
            }
            """);

        InstallHttp(path);

        string text = File.ReadAllText(path).ReplaceLineEndings("\n");

        Assert.Contains("\n  \"inputs\": [\n", text, StringComparison.Ordinal);
        Assert.Contains("\n    {\n      \"type\": \"promptString\",", text, StringComparison.Ordinal);
        Assert.Contains("\n    \"yagu\": {\n      \"type\": \"http\",", text, StringComparison.Ordinal);

        // No blank-line litter and no doubled separators left behind by the splice.
        Assert.DoesNotContain("\n\n\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(",,", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[,", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingTheHttpEntry_LeavesTheFileTidy()
    {
        string path = NewFile("""
            {
              "servers": {
                "other": { "type": "stdio", "command": "other.exe" }
              }
            }
            """);

        InstallHttp(path);
        McpClientRegistrar.Remove(path, Container, ServerName, McpConfigSnippetBuilder.TokenInputId);

        string text = File.ReadAllText(path).ReplaceLineEndings("\n");

        Assert.DoesNotContain("yagu", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(",,", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[,", text, StringComparison.Ordinal);
        Assert.DoesNotContain(",]", text.Replace(" ", string.Empty).Replace("\n", string.Empty), StringComparison.Ordinal);

        using var document = Parse(path);
        Assert.True(document.RootElement.GetProperty(Container).TryGetProperty("other", out _));
    }

    [Theory]
    [InlineData(nameof(McpClientKind.VsCode), "servers")]
    [InlineData(nameof(McpClientKind.VisualStudio), "servers")]
    [InlineData(nameof(McpClientKind.CopilotCli), "mcpServers")]
    [InlineData(nameof(McpClientKind.ClaudeDesktop), "mcpServers")]
    public void EachClientUsesItsOwnContainer(string clientName, string expectedContainer)
    {
        var client = Enum.Parse<McpClientKind>(clientName);
        Assert.Equal(expectedContainer, McpConfigSnippetBuilder.ServersPropertyName(client));

        string path = NewFile();
        string entry = McpConfigSnippetBuilder.BuildServerEntry(client, @"C:\App\Yagu.McpServer.exe", ["--allowed-root", @"C:\Projects"]);

        McpClientRegistrar.Install(path, expectedContainer, McpConfigSnippetBuilder.ServerName, entry);

        using var document = Parse(path);
        Assert.True(document.RootElement.GetProperty(expectedContainer)
            .GetProperty(McpConfigSnippetBuilder.ServerName)
            .TryGetProperty("command", out _));
    }

    [Fact]
    public void EveryClientResolvesToAnAbsoluteUserScopedPath()
    {
        foreach (McpClientKind client in Enum.GetValues<McpClientKind>())
        {
            string path = McpConfigSnippetBuilder.ResolveConfigPath(client);
            Assert.True(Path.IsPathFullyQualified(path), $"{client} resolved to '{path}'.");
        }
    }

    // ── Refusing to write rather than writing something broken ──
    //
    // Every case below feeds the splicer something it cannot produce valid JSON from. The invariant is
    // the same each time: report Malformed and leave the file exactly as it was. A client configuration
    // is a file the user also edits by hand, and a half-spliced one takes out every OTHER server in it.

    private const string BrokenJson = "{ \"type\": ";

    [Fact]
    public void Install_IntoANewFile_RefusesAnUnparseableEntryInsteadOfCreatingIt()
    {
        string path = NewFile();

        McpRegistrationResult result = McpClientRegistrar.Install(path, Container, ServerName, BrokenJson);

        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.False(File.Exists(path), "a refused install must not leave a broken file behind.");
    }

    [Fact]
    public void Install_IntoANewFile_RefusesAnUnparseableTokenInput()
    {
        string path = NewFile();

        McpRegistrationResult result = McpClientRegistrar.Install(
            path, Container, ServerName, Entry, McpConfigSnippetBuilder.TokenInputId, BrokenJson);

        // The entry alone would be valid; the input is what fails. Writing the reference without its
        // definition leaves the client prompting for a variable that does not exist.
        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Install_IntoAnExistingFile_RefusesAnUnparseableEntryAndChangesNothing()
    {
        string existing = """
            {
              "servers": {
                "someone-else": { "command": "other.exe" }
              }
            }
            """;
        string path = NewFile(existing);

        McpRegistrationResult result = McpClientRegistrar.Install(path, Container, ServerName, BrokenJson);

        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.Equal(existing, File.ReadAllText(path));
    }

    [Fact]
    public void Install_IntoAnExistingFile_RefusesAnUnparseableTokenInputAndChangesNothing()
    {
        string existing = """
            {
              "servers": {
                "someone-else": { "command": "other.exe" }
              }
            }
            """;
        string path = NewFile(existing);

        McpRegistrationResult result = McpClientRegistrar.Install(
            path, Container, ServerName, Entry, McpConfigSnippetBuilder.TokenInputId, BrokenJson);

        // The entry splice succeeded before the input failed, so this proves the whole operation is
        // abandoned rather than half-committed.
        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.Equal(existing, File.ReadAllText(path));
    }

    [Fact]
    public void Install_ReplacingAnExistingEntry_RefusesAnUnparseableReplacement()
    {
        string path = NewFile();
        McpClientRegistrar.Install(path, Container, ServerName, Entry);
        string installed = File.ReadAllText(path);

        McpRegistrationResult result = McpClientRegistrar.Install(path, Container, ServerName, BrokenJson);

        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.Equal(installed, File.ReadAllText(path));
    }

    // ── RemoveInput ──

    [Fact]
    public void RemoveInput_FromAFileThatDoesNotExist_IsNotPresent()
    {
        McpRegistrationResult result = McpClientRegistrar.RemoveInput(
            Path.Combine(_directory, "absent.json"), McpConfigSnippetBuilder.TokenInputId);

        Assert.Equal(McpRegistrationOutcome.NotPresent, result.Outcome);
    }

    [Fact]
    public void RemoveInput_FromAFileWithoutThatInput_LeavesItByteIdentical()
    {
        string existing = """
            {
              "inputs": [ { "id": "someone-elses-secret", "type": "promptString" } ],
              "servers": { "someone-else": { "command": "other.exe" } }
            }
            """;
        string path = NewFile(existing);

        McpRegistrationResult result = McpClientRegistrar.RemoveInput(path, McpConfigSnippetBuilder.TokenInputId);

        // Rewriting here would reformat a file for no reason and churn the user's source control.
        Assert.Equal(McpRegistrationOutcome.NotPresent, result.Outcome);
        Assert.Equal(existing, File.ReadAllText(path));
    }

    [Fact]
    public void RemoveInput_FromAMalformedFile_RefusesRatherThanRepairing()
    {
        const string Garbage = "{ \"inputs\": [ { \"id\": ";
        string path = NewFile(Garbage);

        McpRegistrationResult result = McpClientRegistrar.RemoveInput(path, McpConfigSnippetBuilder.TokenInputId);

        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.Equal(Garbage, File.ReadAllText(path));
    }

    [Fact]
    public void RemoveInput_TakesOutOnlyYagusDefinition()
    {
        string path = NewFile("""
            {
              "inputs": [ { "id": "someone-elses-secret", "type": "promptString" } ],
              "servers": { "someone-else": { "command": "other.exe" } }
            }
            """);
        InstallHttp(path);
        Assert.Contains(McpConfigSnippetBuilder.TokenInputId, InputIds(path), StringComparer.Ordinal);

        McpRegistrationResult result = McpClientRegistrar.RemoveInput(path, McpConfigSnippetBuilder.TokenInputId);

        Assert.Equal(McpRegistrationOutcome.Removed, result.Outcome);

        // Only the DEFINITION goes. The ${input:} reference inside the server entry is removed by
        // whatever rewrites that entry, so it is still present here and must not be mistaken for failure.
        string[] ids = InputIds(path);
        Assert.DoesNotContain(McpConfigSnippetBuilder.TokenInputId, ids, StringComparer.Ordinal);
        Assert.Contains("someone-elses-secret", ids, StringComparer.Ordinal);
    }

    /// <summary>The ids declared in the file's <c>inputs</c> array.</summary>
    private static string[] InputIds(string path)
    {
        using System.Text.Json.JsonDocument document = Parse(path);
        if (!document.RootElement.TryGetProperty(McpConfigSnippetBuilder.InputsPropertyName, out var inputs))
            return [];

        return [.. inputs.EnumerateArray()
            .Select(input => input.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty)];
    }

    // ── Remove ──

    [Fact]
    public void Remove_FromAMalformedFile_RefusesRatherThanRepairing()
    {
        const string Garbage = "{ \"servers\": { \"yagu\": ";
        string path = NewFile(Garbage);

        McpRegistrationResult result = McpClientRegistrar.Remove(path, Container, ServerName);

        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.Equal(Garbage, File.ReadAllText(path));
    }

    [Fact]
    public void Remove_WithAMalformedInputsSection_RefusesAfterTheEntryWouldHaveGone()
    {
        // The server entry is removable and the inputs array is not, so this proves the entry removal is
        // rolled back with it rather than written and then reported as a failure.
        string original = """
            {
              "servers": { "yagu": { "command": "Yagu.McpServer.exe" } },
              "inputs": [ { "id": 
            }
            """;
        string path = NewFile(original);

        McpRegistrationResult result = McpClientRegistrar.Remove(
            path, Container, ServerName, McpConfigSnippetBuilder.TokenInputId);

        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.Equal(original, File.ReadAllText(path));
    }

    // ── IsInstalled ──

    [Fact]
    public void IsInstalled_IsFalseForAMalformedFileRatherThanThrowing()
    {
        string path = NewFile("{ \"servers\": ");

        // This runs while the Settings tab is being built, so an exception here takes the window with it.
        Assert.False(McpClientRegistrar.IsInstalled(path, Container, ServerName));
    }

    [Fact]
    public void IsInstalled_IsFalseWhenThePathIsADirectory()
    {
        Assert.False(McpClientRegistrar.IsInstalled(_directory, Container, ServerName));
    }

    // ── The atomic write, and what it refuses ──
    //
    // A client configuration is a file the user also has open in an editor. Everything below is about
    // never discarding a save that happened between Yagu reading the file and writing it back.

    [Fact]
    public void Write_RefusesWhenTheFileGrewSinceItWasRead()
    {
        string path = NewFile("""{ "servers": {} }""");
        var info = new FileInfo(path);

        McpRegistrationResult result = McpClientRegistrar.Write(
            path, "{}", info.LastWriteTimeUtc, info.Length + 1, McpRegistrationOutcome.Updated);

        Assert.Equal(McpRegistrationOutcome.Failed, result.Outcome);
        Assert.Contains("changed while it was being edited", result.Detail);
        Assert.Contains("nothing was written", result.Detail);
    }

    [Fact]
    public void Write_RefusesWhenTheFileWasSavedSinceItWasRead()
    {
        string path = NewFile("""{ "servers": {} }""");
        var info = new FileInfo(path);

        McpRegistrationResult result = McpClientRegistrar.Write(
            path, "{}", info.LastWriteTimeUtc.AddSeconds(-1), info.Length, McpRegistrationOutcome.Updated);

        Assert.Equal(McpRegistrationOutcome.Failed, result.Outcome);
        Assert.Contains("changed while it was being edited", result.Detail);
    }

    [Fact]
    public void Write_RefusesWhenAFileAppearedWhereThereWasNone()
    {
        // A -1 baseline means "this write must CREATE the file". Something else got there first — another
        // Yagu, or the client writing its own first configuration — and replacing it would wipe it.
        string path = NewFile("""{ "servers": { "someone-else": {} } }""");

        McpRegistrationResult result = McpClientRegistrar.Write(
            path, "{}", default, -1, McpRegistrationOutcome.Installed);

        Assert.Equal(McpRegistrationOutcome.Failed, result.Outcome);
        Assert.Contains("was created while it was being edited", result.Detail);
        Assert.Contains("someone-else", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RefusesWhenTheFileWasDeletedSinceItWasRead()
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");

        McpRegistrationResult result = McpClientRegistrar.Write(
            path, "{}", DateTime.UtcNow, 42, McpRegistrationOutcome.Updated);

        Assert.Equal(McpRegistrationOutcome.Failed, result.Outcome);
        Assert.Contains("was removed while it was being edited", result.Detail);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Install_ClearsAScratchFileLeftBehindByAKilledRun()
    {
        string path = NewFile("""{ "servers": {} }""");
        string stale = path + McpClientRegistrar.TempSuffix;
        File.WriteAllText(stale, "left over from a run that was killed mid-write");

        McpRegistrationResult result = McpClientRegistrar.Install(path, Container, ServerName, Entry);

        Assert.Equal(McpRegistrationOutcome.Installed, result.Outcome);
        Assert.False(File.Exists(stale), "the scratch file must not survive beside the user's configuration.");
    }

    [Fact]
    public void Install_KeepsTheUsersPreviousConfigurationAsABackup()
    {
        string path = NewFile("""{ "servers": { "someone-else": { "command": "other.exe" } } }""");

        McpClientRegistrar.Install(path, Container, ServerName, Entry);

        // The splice is checked for well-formedness but not for meaning, so this is the only recovery
        // path if it ever produces valid JSON that says the wrong thing.
        string backup = path + McpClientRegistrar.BackupSuffix;
        Assert.True(File.Exists(backup));
        Assert.Contains("someone-else", File.ReadAllText(backup), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_WhenSomethingElseHoldsTheFileOpen_FailsWithoutLeavingAScratchFile()
    {
        string path = NewFile("""{ "servers": {} }""");

        // Models a scanner or indexer holding the destination. File.Replace retries and then gives up.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        McpRegistrationResult result = McpClientRegistrar.Install(path, Container, ServerName, Entry);

        Assert.Equal(McpRegistrationOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(path + McpClientRegistrar.TempSuffix));
    }

    // ── Comments that sit exactly where the cut has to be made ──

    [Fact]
    public void Remove_WithABlockCommentBetweenTheValueAndItsComma_LeavesValidJson()
    {
        // Stepping over trivia after the value is what finds the comma that has to go with it. A block
        // comment is the form a `//` scan walks straight past, taking the rest of the line with it.
        string path = NewFile("""
            {
              "servers": {
                "yagu": { "type": "stdio", "command": "yagu.exe" } /* installed
                   by Yagu */,
                "other": { "command": "o" }
              }
            }
            """);

        McpRegistrationResult result = McpClientRegistrar.Remove(path, Container, ServerName);

        Assert.Equal(McpRegistrationOutcome.Removed, result.Outcome);

        using var document = Parse(path);
        var servers = document.RootElement.GetProperty(Container);
        Assert.False(servers.TryGetProperty(ServerName, out _));
        Assert.True(servers.TryGetProperty("other", out _));
    }

    [Fact]
    public void Remove_WithACommentImmediatelyBeforeTheEntry_DoesNotStrandADanglingComma()
    {
        // The cut has to start before the user's note about the entry, or the comment is left behind
        // describing a server that is gone — and the comma in front of it disables the next one.
        string path = NewFile("""
            {
              "servers": {
                "other": { "command": "o" },
                // added by Yagu
                "yagu": { "type": "stdio", "command": "yagu.exe" }
              }
            }
            """);

        McpRegistrationResult result = McpClientRegistrar.Remove(path, Container, ServerName);

        Assert.Equal(McpRegistrationOutcome.Removed, result.Outcome);

        using var document = Parse(path);
        var servers = document.RootElement.GetProperty(Container);
        Assert.False(servers.TryGetProperty(ServerName, out _));
        Assert.True(servers.TryGetProperty("other", out _));
    }

    [Fact]
    public void Remove_WithABlockCommentImmediatelyBeforeTheEntry_LeavesValidJson()
    {
        string path = NewFile("""
            {
              "servers": {
                "other": { "command": "o" },
                /* added by Yagu on
                   some Tuesday */
                "yagu": { "type": "stdio", "command": "yagu.exe" }
              }
            }
            """);

        McpRegistrationResult result = McpClientRegistrar.Remove(path, Container, ServerName);

        Assert.Equal(McpRegistrationOutcome.Removed, result.Outcome);

        using var document = Parse(path);
        Assert.True(document.RootElement.GetProperty(Container).TryGetProperty("other", out _));
    }

    [Fact]
    public void Install_ReplacingAnEntryWrappedInComments_KeepsBothOfThem()
    {
        string path = NewFile("""
            {
              "servers": {
                /* before */
                "yagu": { "type": "stdio", "command": "old.exe" } /* after */,
                "other": { "command": "o" }
              }
            }
            """);

        McpRegistrationResult result = McpClientRegistrar.Install(path, Container, ServerName, Entry);

        Assert.Equal(McpRegistrationOutcome.Updated, result.Outcome);

        string text = File.ReadAllText(path);
        Assert.Contains("/* before */", text, StringComparison.Ordinal);
        Assert.Contains("/* after */", text, StringComparison.Ordinal);
        Assert.DoesNotContain("old.exe", text, StringComparison.Ordinal);
    }
}
