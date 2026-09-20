namespace YALA.Tests;

public sealed class ReconnectExperienceTests
{
    [Fact]
    public void App_UsesFastBackoffReconnectSchedule()
    {
        var app = ReadRepositoryFile("src", "YALA", "Components", "App.razor");

        Assert.Contains("autostart=\"false\"", app, StringComparison.Ordinal);
        Assert.Contains("maxRetries: 6", app, StringComparison.Ordinal);
        Assert.Contains("Array.prototype.at.bind([0, 1000, 2000, 5000, 10000, 30000])", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectStatus_IsANonModalBanner()
    {
        var markup = ReadRepositoryFile("src", "YALA", "Components", "Layout", "ReconnectModal.razor");
        var script = ReadRepositoryFile("src", "YALA", "Components", "Layout", "ReconnectModal.razor.js");
        var styles = ReadRepositoryFile("src", "YALA", "Components", "Layout", "ReconnectModal.razor.css");

        Assert.Contains("<aside id=\"components-reconnect-modal\"", markup, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<dialog", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("showModal", script, StringComparison.Ordinal);
        Assert.Contains("const reconnectBannerDelayMilliseconds = 5000", script, StringComparison.Ordinal);
        Assert.Contains("scheduleReconnectBanner();", script, StringComparison.Ordinal);
        Assert.Contains("hideReconnectBanner();", script, StringComparison.Ordinal);
        Assert.Contains("position: fixed", styles, StringComparison.Ordinal);
        Assert.Contains("inset: 0 0 auto", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("::backdrop", styles, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YALA.slnx")))
        {
            directory = directory.Parent;
        }

        var repositoryRoot = directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find the YALA repository root.");

        return File.ReadAllText(Path.Combine([repositoryRoot, .. pathSegments]));
    }
}
