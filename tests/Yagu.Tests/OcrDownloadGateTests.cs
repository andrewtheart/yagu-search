using Yagu.Services.Ocr;

namespace Yagu.Tests;

/// <summary>
/// Covers <see cref="OcrDownloadGate"/>: the single consent decision point that authorizes (or
/// refuses) external OCR asset downloads. The gate uses process-global static state
/// (<see cref="OcrDownloadGate.ConsentGranted"/> / <see cref="OcrDownloadGate.PromptAsync"/>), so
/// these tests run in their own non-parallel collection and reset the statics around each case.
/// </summary>
[Collection("OcrDownloadGate")]
public sealed class OcrDownloadGateTests : IDisposable
{
    public OcrDownloadGateTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        OcrDownloadGate.ConsentGranted = false;
        OcrDownloadGate.PromptAsync = null;
    }

    private static OcrAssetRequirement Requirement(bool downloadNeeded, long bytes = 349L * 1024 * 1024)
        => new()
        {
            EngineDisplayName = "PaddleSharp",
            DownloadNeeded = downloadNeeded,
            ApproxBytes = downloadNeeded ? bytes : 0,
            MissingComponents = downloadNeeded
                ? new[] { "OCR engine runtime (~349 MB)" }
                : Array.Empty<string>(),
        };

    [Fact]
    public void ApproxMb_RoundsUpFromBytes()
    {
        var req = Requirement(downloadNeeded: true, bytes: 17L * 1024 * 1024);
        Assert.Equal(17, req.ApproxMb);
    }

    [Fact]
    public async Task EnsureAllowed_TrueWhenNothingMissing_WithoutPrompting()
    {
        bool prompted = false;
        OcrDownloadGate.PromptAsync = _ => { prompted = true; return Task.FromResult(true); };

        bool allowed = await OcrDownloadGate.EnsureAllowedAsync(Requirement(downloadNeeded: false));

        Assert.True(allowed);
        Assert.False(prompted);
    }

    [Fact]
    public async Task EnsureAllowed_TrueWhenAlreadyConsented_WithoutPrompting()
    {
        OcrDownloadGate.ConsentGranted = true;
        bool prompted = false;
        OcrDownloadGate.PromptAsync = _ => { prompted = true; return Task.FromResult(false); };

        bool allowed = await OcrDownloadGate.EnsureAllowedAsync(Requirement(downloadNeeded: true));

        Assert.True(allowed);
        Assert.False(prompted);
    }

    [Fact]
    public async Task EnsureAllowed_FalseWhenDownloadNeededButNoPromptRegistered()
    {
        // Headless host (CLI/tests) with no UI hook: refuse rather than download silently.
        bool allowed = await OcrDownloadGate.EnsureAllowedAsync(Requirement(downloadNeeded: true));

        Assert.False(allowed);
        Assert.False(OcrDownloadGate.ConsentGranted);
    }

    [Fact]
    public async Task EnsureAllowed_PromptsAndRemembersApproval()
    {
        int prompts = 0;
        OcrDownloadGate.PromptAsync = _ => { prompts++; return Task.FromResult(true); };

        bool first = await OcrDownloadGate.EnsureAllowedAsync(Requirement(downloadNeeded: true));
        bool second = await OcrDownloadGate.EnsureAllowedAsync(Requirement(downloadNeeded: true));

        Assert.True(first);
        Assert.True(second);
        Assert.True(OcrDownloadGate.ConsentGranted);
        Assert.Equal(1, prompts); // second call short-circuits on remembered consent
    }

    [Fact]
    public async Task EnsureAllowed_DeclinedPrompt_DoesNotGrantConsent()
    {
        OcrDownloadGate.PromptAsync = _ => Task.FromResult(false);

        bool allowed = await OcrDownloadGate.EnsureAllowedAsync(Requirement(downloadNeeded: true));

        Assert.False(allowed);
        Assert.False(OcrDownloadGate.ConsentGranted);
    }

    [Fact]
    public async Task EnsureAllowed_ConcurrentCallers_PromptAtMostOnce()
    {
        int prompts = 0;
        var gate = new TaskCompletionSource<bool>();
        OcrDownloadGate.PromptAsync = async _ =>
        {
            Interlocked.Increment(ref prompts);
            return await gate.Task.ConfigureAwait(false);
        };

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => OcrDownloadGate.EnsureAllowedAsync(Requirement(downloadNeeded: true)))
            .ToArray();

        // Let the first caller enter the prompt, then approve.
        gate.SetResult(true);
        bool[] results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);
        Assert.Equal(1, prompts);
        Assert.True(OcrDownloadGate.ConsentGranted);
    }
}
