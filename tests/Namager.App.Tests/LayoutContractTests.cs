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

    /// <summary>Delete is destructive and was too easy to hit sitting next to the constructive
    /// buttons. All three list toolbars must dock it to the right, under a name the code-behind
    /// can attach a confirmation to.</summary>
    [Theory, MemberData(nameof(ListViews))]
    public void List_view_docks_a_named_delete_button_to_the_right(string file)
    {
        var xaml = View(file);
        Assert.Contains("x:Name=\"DeleteButton\"", xaml);
        Assert.Contains("DockPanel.Dock=\"Right\"", xaml);
        // Bound Command would fire without the confirmation the code-behind adds.
        Assert.DoesNotContain("Command=\"{Binding DeleteCommand}\"", xaml);
    }

    /// <summary>Every delete path routes through a confirmation dialog before the command runs.</summary>
    [Theory]
    [InlineData("PresetListView.axaml.cs")]
    [InlineData("AmpListView.axaml.cs")]
    [InlineData("IrListView.axaml.cs")]
    public void List_view_confirms_before_deleting(string file)
    {
        var cs = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Namager.App", "Views", file));
        Assert.Contains("ConfirmDialog.ShowAsync", cs);
        Assert.Contains("DeleteCommand", cs);
    }

    /// <summary>The unsaved-changes dot must sit next to Save, so Download comes first.</summary>
    [Fact]
    public void Editor_toolbar_orders_download_then_save_then_the_dirty_dot()
    {
        var xaml = View("ParameterEditorView.axaml");
        int download = xaml.IndexOf("Icon.Download", StringComparison.Ordinal);
        int save = xaml.IndexOf("Icon.Save", StringComparison.Ordinal);
        int dot = xaml.IndexOf("IsVisible=\"{Binding IsDirty}\"", StringComparison.Ordinal);
        Assert.True(download >= 0 && save >= 0 && dot >= 0);
        Assert.True(download < save, "Download must precede Save.");
        Assert.True(save < dot, "The dirty dot must follow Save.");
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
