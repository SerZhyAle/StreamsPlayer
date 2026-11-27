using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0275: what the console companion is made of and where it ships, held as text - the project file and sources, the
// application project's publish hook, the MSIX template, the installer's copy line and the three gate call sites. None of it
// is loaded or compiled here (the test project references the core and nothing else); a build, a package and a published
// folder prove the rest at the release.
public sealed class ConsoleCompanionProjectTests
{
    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, .. path]));

    private static XDocument Project(params string[] path) => XDocument.Parse(Read(path));

    [Fact]
    public void TheCompanionIsAConsoleProgramThatReferencesOnlyTheCore()
    {
        var project = Project("cli-source", "StreamsPlayer.Cli.csproj");
        string? Property(string name) => project.Descendants(name).Select(element => element.Value).SingleOrDefault();

        Assert.Equal("Exe", Property("OutputType"));
        Assert.Equal("net10.0", Property("TargetFramework"));
        Assert.Equal("StreamsPlayerCli", Property("AssemblyName"));
        Assert.Null(Property("UseWPF"));
        Assert.Null(Property("UseWindowsForms"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("Reference"));
        var reference = Assert.Single(project.Descendants("ProjectReference"));
        Assert.EndsWith("StreamsPlayer.Core.csproj", (string)reference.Attribute("Include")!, StringComparison.Ordinal);
        Assert.Equal(AppCommandConsole.ExecutableName, Property("AssemblyName") + ".exe");
    }

    [Fact]
    public void TheCompanionSourcesNameNoWindowToolkitAndNoApplicationCode()
    {
        var sources = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "cli-source"), "*.cs");

        Assert.NotEmpty(sources);
        Assert.All(sources, file =>
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("System.Windows", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PresentationFramework", text, StringComparison.Ordinal);
            Assert.DoesNotContain("StreamsPlayer.App", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheCompanionsEntryPointOnlyHandsTheCoreTheProcessesOwnStreams()
    {
        var entry = Read("cli-source", "Program.cs");

        Assert.Contains("AppCommandConsole.Run(", entry, StringComparison.Ordinal);
        Assert.Contains("AppDataProfile.Identity()", entry, StringComparison.Ordinal);
        Assert.Contains("Console.OpenStandardOutput()", entry, StringComparison.Ordinal);
        Assert.True(entry.Split('\n').Length < 40, "the entry point stays a Main; behaviour belongs to the core");
    }

    [Fact]
    public void TheApplicationProjectBuildsTheCompanionAndPublishesItIntoItsOwnPublishFolder()
    {
        var project = Project("app-source", "StreamsPlayer.App.csproj");

        var reference = Assert.Single(project.Descendants("ProjectReference"),
            element => ((string)element.Attribute("Include")!).EndsWith("StreamsPlayer.Cli.csproj", StringComparison.Ordinal));
        Assert.Equal("false", (string?)reference.Attribute("ReferenceOutputAssembly"));

        var target = Assert.Single(project.Descendants("Target"), element => (string?)element.Attribute("Name") == "PublishConsoleCompanion");
        Assert.Equal("Publish", (string?)target.Attribute("AfterTargets"));
        var publish = Assert.Single(target.Descendants("MSBuild"));
        Assert.EndsWith("StreamsPlayer.Cli.csproj", (string)publish.Attribute("Projects")!, StringComparison.Ordinal);
        Assert.Equal("Publish", (string?)publish.Attribute("Targets"));
        Assert.Contains("NoBuild=true", (string)publish.Attribute("Properties")!, StringComparison.Ordinal);
        Assert.Contains("PublishDir=", (string)publish.Attribute("Properties")!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerCopiesTheWholePublishFolderSoTheCompanionNeedsNoLineOfItsOwn()
    {
        var installer = Read("installer", "StreamsPlayer.iss");

        Assert.Contains(
            "Source: \"{#SourceDir}\\*\"; DestDir: \"{app}\"; Flags: recursesubdirs createallsubdirs ignoreversion", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMsixTemplateAddsTheCompanionAsASecondApplicationAndLeavesTheFirstOneAsItWas()
    {
        var manifest = Project("msix", "AppxManifest.xml");
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        XNamespace uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";

        var applications = manifest.Descendants(foundation + "Application").ToArray();
        Assert.Equal(new[] { "StreamsPlayer", "StreamsPlayerCli" }, applications.Select(application => (string)application.Attribute("Id")!));

        var player = applications[0];
        Assert.Equal("StreamsPlayer.exe", (string)player.Attribute("Executable")!);
        Assert.Equal("StreamsPlayer.exe", (string)player.Descendants(uap5 + "ExecutionAlias").Single().Attribute("Alias")!);
        Assert.Single(player.Descendants(uap + "FileType"), element => element.Value == ".spres");

        var companion = applications[1];
        Assert.Equal(AppCommandConsole.ExecutableName, (string)companion.Attribute("Executable")!);
        Assert.Equal("Windows.FullTrustApplication", (string)companion.Attribute("EntryPoint")!);
        Assert.Equal("none", (string)companion.Descendants(uap + "VisualElements").Single().Attribute("AppListEntry")!);
        var alias = companion.Descendants(uap5 + "ExecutionAlias").Single();
        Assert.Equal(AppCommandConsole.ExecutableName, (string)alias.Attribute("Alias")!);
        Assert.Equal(AppCommandConsole.ExecutableName, (string)alias.Parent!.Parent!.Attribute("Executable")!);

        // The identity is not this ticket's: its three values stay the placeholders the build fills from msix/identity.json.
        var identity = manifest.Descendants(foundation + "Identity").Single();
        Assert.Equal("__IDENTITY_NAME__", (string)identity.Attribute("Name")!);
        Assert.Equal("__PUBLISHER__", (string)identity.Attribute("Publisher")!);
    }

    [Fact]
    public void EveryCommandTheCompanionServesIsWrittenForTheCompanionInTheManifest()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "docs", "cli-commands.json")));
        var served = document.RootElement.GetProperty("commands").EnumerateArray()
            .Where(command => command.TryGetProperty("exe", out _)).ToArray();

        Assert.Equal(13, served.Length);
        Assert.All(served, command =>
        {
            Assert.Equal(AppCommandConsole.ExecutableName, command.GetProperty("exe").GetString());
            Assert.StartsWith(AppCommandConsole.ExecutableName + " ", command.GetProperty("synopsis").GetString(), StringComparison.Ordinal);
            Assert.All(command.GetProperty("examples").EnumerateArray(),
                example => Assert.Contains(AppCommandConsole.ExecutableName, example.GetProperty("line").GetString(), StringComparison.Ordinal));
        });
        Assert.Equal("StreamsPlayer.exe", document.RootElement.GetProperty("exe").GetString());
    }

    [Fact]
    public void ThePayloadGateRunsWhereThePinnedNativesGateRuns()
    {
        var build = Read("build.ps1");
        var msix = Read("msix", "build-msix.ps1");
        var release = Read("workflows", "release.yml");

        Assert.Contains("function Assert-ConsoleCompanion", build, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(build, @"^\s+Assert-ConsoleCompanion -Folder ", RegexOptions.Multiline).Count);
        Assert.Contains("scripts/Assert-ConsoleCompanion.ps1') -Folder $publish", msix, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"Assert-ConsoleCompanion\.ps1 -Folder stage/StreamsPlayer"), release);
    }

    [Fact]
    public void TheGateScriptJudgesTheHostFilesTheToolkitNamesAndTheVersion()
    {
        var gate = Read("scripts", "Assert-ConsoleCompanion.ps1");

        Assert.Contains("StreamsPlayerCli.exe", gate, StringComparison.Ordinal);
        Assert.Contains("StreamsPlayerCli.deps.json", gate, StringComparison.Ordinal);
        Assert.Contains("StreamsPlayerCli.runtimeconfig.json", gate, StringComparison.Ordinal);
        Assert.Contains("PresentationFramework", gate, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsDesktop.App", gate, StringComparison.Ordinal);
        Assert.Contains("ProductVersion", gate, StringComparison.Ordinal);
    }
}
