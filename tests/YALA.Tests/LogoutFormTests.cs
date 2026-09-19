namespace YALA.Tests;

public sealed class LogoutFormTests
{
    [Fact]
    public void MainLayout_UsesRelativeLogoutReturnUrl()
    {
        var repositoryRoot = FindRepositoryRoot();
        var layoutPath = Path.Combine(repositoryRoot, "src", "YALA", "Components", "Layout", "MainLayout.razor");
        var layout = File.ReadAllText(layoutPath);

        Assert.Contains("name=\"ReturnUrl\" value=\"Account/Login\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"ReturnUrl\" value=\"/Account/Login\"", layout, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YALA.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find the YALA repository root.");
    }
}
