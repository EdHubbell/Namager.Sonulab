using System.Runtime.CompilerServices;

namespace Namager.App.Tests;

/// <summary>Guards the layout contract from docs/superpowers/specs/2026-07-24-tab-layout-alignment-design.md:
/// the three list tabs must take their toolbar/list spacing from the shared style classes, never from
/// literals. Without this, a well-meaning edit re-hardcodes a margin and the tabs silently drift apart
/// again. Reads the .axaml as text — the App test project has no Avalonia.Headless reference and this
/// deliberately does not add one.</summary>
public class LayoutContractTests
{
    internal static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string View(string name)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Namager.App", "Views", name));

    public static TheoryData<string> ListViews => new()
    {
        "PresetListView.axaml", "AmpListView.axaml", "IrListView.axaml",
    };

    [Theory, MemberData(nameof(ListViews))]
    public void List_view_uses_the_shared_toolbar_and_list_classes(string file)
    {
        var xaml = View(file);
        Assert.Contains("Classes=\"slot-toolbar\"", xaml);
        Assert.Contains("Classes=\"slot-list\"", xaml);
    }

    [Theory, MemberData(nameof(ListViews))]
    public void List_view_has_no_hardcoded_toolbar_or_list_spacing(string file)
    {
        var xaml = View(file);
        Assert.DoesNotContain("Margin=\"8,6,8,4\"", xaml);   // old Presets/IRs toolbar literal
        Assert.DoesNotContain("Margin=\"0,0,0,6\"", xaml);   // old Amps toolbar literal
        Assert.DoesNotContain("Margin=\"8,0\"", xaml);       // old list + IR message literal
    }

    [Theory, MemberData(nameof(ListViews))]
    public void List_view_does_not_redeclare_the_reorder_button_style(string file)
        => Assert.DoesNotContain("Selector=\"Button.reorder\"", View(file));

    [Fact]
    public void Theme_defines_the_layout_tokens()
    {
        var theme = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Namager.App", "Styles", "SonulabTheme.axaml"));
        Assert.Contains("x:Key=\"Sonulab.PageInset\"", theme);
        Assert.Contains("x:Key=\"Sonulab.ListInset\"", theme);
        Assert.Contains("x:Key=\"Sonulab.PaneGap\"", theme);
        Assert.Contains("x:Key=\"Sonulab.ToolbarHeight\"", theme);
    }

    [Fact]
    public void Amp_detail_pane_reserves_the_toolbar_band_instead_of_a_magic_offset()
    {
        var detail = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Namager.App", "Views", "AmpDetailPanel.axaml"));
        Assert.Contains("Classes=\"slot-toolbar\"", detail);

        var amps = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Namager.App", "Views", "AmpListView.axaml"));
        Assert.DoesNotContain("Margin=\"16,34,0,0\"", amps);   // the retired magic offset
    }

    [Fact]
    public void Parameter_editor_toolbar_takes_the_shared_toolbar_class()
    {
        var xaml = View("ParameterEditorView.axaml");
        Assert.Contains("Classes=\"slot-toolbar\"", xaml);
    }

    [Fact]
    public void Both_detail_panes_take_the_same_gap_token()
    {
        foreach (var file in new[] { "AmpListView.axaml", "MainWindow.axaml" })
        {
            var xaml = File.ReadAllText(
                Path.Combine(RepoRoot(), "src", "Namager.App", "Views", file));
            Assert.Contains("Sonulab.PaneGap", xaml);
        }
    }
}
