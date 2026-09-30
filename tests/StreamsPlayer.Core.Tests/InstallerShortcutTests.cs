using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

// A fresh install must leave a Start menu group and a desktop shortcut. The group is unconditional; the
// desktop shortcut hangs on a task that must stay checked by default, because a silent (winget) install
// takes the default and never shows the wizard.
public sealed class InstallerShortcutTests
{
    private static string Script =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "installer", "StreamsPlayer.iss"));

    [Fact]
    public void StartMenuGroupShortcut_IsCreatedWithoutATask()
    {
        var line = Regex.Match(Script, @"^Name: ""\{group\}\\STREAMS Player""; Filename: ""\{app\}\\StreamsPlayer\.exe"".*$", RegexOptions.Multiline);

        Assert.True(line.Success, "installer/StreamsPlayer.iss no longer creates the Start menu shortcut.");
        Assert.DoesNotContain("Tasks:", line.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopShortcut_IsCreatedByADefaultCheckedTask()
    {
        var script = Script;
        var icon = Regex.Match(script, @"^Name: ""\{autodesktop\}\\STREAMS Player""; Filename: ""\{app\}\\StreamsPlayer\.exe""; Tasks: desktopicon\s*$", RegexOptions.Multiline);
        var task = Regex.Match(script, @"^Name: ""desktopicon"";.*$", RegexOptions.Multiline);

        Assert.True(icon.Success, "installer/StreamsPlayer.iss no longer creates the desktop shortcut.");
        Assert.True(task.Success, "installer/StreamsPlayer.iss no longer declares the desktopicon task.");
        Assert.DoesNotContain("unchecked", task.Value, StringComparison.OrdinalIgnoreCase);
    }
}
