namespace YALA.Tests;

public sealed class ReconnectExperienceTests
{
    [Fact]
    public void App_DoesNotStartABlazorServerCircuit()
    {
        var app = ReadRepositoryFile("src", "YALA", "Components", "App.razor");

        Assert.Contains("_framework/blazor.web.js", app, StringComparison.Ordinal);
        Assert.DoesNotContain("autostart=\"false\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Blazor.start", app, StringComparison.Ordinal);
        Assert.DoesNotContain("reconnectionOptions", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectStatus_IsANonModalBanner()
    {
        var layout = ReadRepositoryFile("src", "YALA.Client", "Components", "Layout", "MainLayout.razor");

        Assert.Contains("connection-banner", layout, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<dialog", layout, StringComparison.Ordinal);
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
