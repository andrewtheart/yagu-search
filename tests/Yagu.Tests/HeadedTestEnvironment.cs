namespace Yagu.Tests;

/// <summary>
/// Detects whether the current machine can run "headed" UI-automation tests — the ones that launch
/// the WinUI 3 app and drive it through UIAutomation. Those tests **auto-run** on an interactive
/// Windows desktop and **auto-skip** where GUI automation can't work or is unreliable: CI runners
/// (GitHub Actions / Azure DevOps / GitLab / Jenkins), non-interactive sessions, and non-Windows.
///
/// This is the gate for the <c>Headed</c> test category: rather than requiring an explicit
/// <c>--filter "Category=Headed"</c> or an opt-in env var, a headed test checks <see cref="CanRun"/>
/// at runtime and skips itself when the environment can't support it — so a normal <c>dotnet test</c>
/// run on a dev desktop exercises them, while the same run in CI quietly skips them.
/// </summary>
internal static class HeadedTestEnvironment
{
    /// <summary>True when headed UI-automation tests can run here: interactive Windows desktop, not CI.</summary>
    public static bool CanRun =>
        !IsContinuousIntegration()
        && OperatingSystem.IsWindows()
        && Environment.UserInteractive;

    /// <summary>Human-readable reason headed tests are being skipped, or empty when they can run.</summary>
    public static string SkipReason
    {
        get
        {
            if (IsContinuousIntegration())
                return "running under CI — GUI automation is unreliable on CI runners (e.g. GitHub Actions).";
            if (!OperatingSystem.IsWindows())
                return "not Windows.";
            if (!Environment.UserInteractive)
                return "no interactive desktop session.";
            return string.Empty;
        }
    }

    private static bool IsContinuousIntegration()
        => IsEnvTrue("GITHUB_ACTIONS")   // GitHub Actions
        || IsEnvTrue("CI")               // generic CI convention (GitHub, GitLab, Travis, CircleCI, …)
        || IsEnvTrue("TF_BUILD")         // Azure DevOps / Azure Pipelines
        || IsEnvTrue("GITLAB_CI")        // GitLab CI
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JENKINS_URL")); // Jenkins

    private static bool IsEnvTrue(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }
}
