# Modulation Block + Recursive Parameter Nesting — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the pedal's Modulation block editable in NAMager, rendered between Impulse Response and Delay, with every nested folder (Tremolo, Tone and Character, Rate) getting its own expander.

**Architecture:** Collapse `BlockSectionViewModel` and `SubGroupViewModel` into one recursive `ParameterGroupViewModel` used at every depth; replace the editor's fixed one-level grouping test with a generic walk that builds groups from path depth in firmware browse order; render with a self-instantiating `ParameterGroupView` UserControl. Then add `"mod"` to `Blocks_InScope`.

**Tech Stack:** .NET 10, C#, Avalonia 12 (built-in FluentTheme), CommunityToolkit.Mvvm, xUnit.

## Global Constraints

- **Avalonia 12 + built-in `FluentTheme`. Do NOT add FluentAvalonia** — it targets Avalonia 11 and crashes at runtime on 12. Icons are built-in `PathIcon` geometries.
- **No hex colour literals in `.axaml`** — use `Styles/SonulabTheme.axaml` tokens (`Sonulab.*Brush`). The firmware's `style:"color:#00FFFF;"` hints on `mod`/`trfolder` are explicitly NOT used.
- `dotnet build` must introduce **no new warnings** (the baseline is not warning-clean — the test project already emits CA1416 and xUnit analyzer warnings); `dotnet test` must stay green (1054 tests before this work).
- Parameter exposure is a **blocklist** (`hidden-params.json`), never an allowlist — new firmware params must auto-appear.
- Paths in this codebase use **backslash** separators (`root\app\mod\trfolder`) and are compared with `StringComparison.Ordinal`.
- Editable node types are exactly `float`, `enum`, `plist` (`ParameterEditorViewModel.EditableTypes`). Everything else (`item`) is a container.
- No device writes are introduced by this work. Existing save behaviour (write dirty fields, then `save`) is unchanged.

---

## Reference: the real firmware tree

Captured from the pedal with `dotnet run --project tools/HwCheck -- --browse root\app\mod` (fw 2.5.1, 24 records). Tasks 4–6 use this verbatim as a test fixture. Records arrive in this exact order:

```
root\app\mod: {"desc":"Mod","value":"","type":"item","style":"color:#00FFFF;","item_type":"hfolder","def":""}
root\app\mod\on_off: {"desc":"Enable","value":"OFF","type":"enum","def":"OFF","options":["ON","OFF"]}
root\app\mod\mode: {"desc":"Mode","value":"Chorus","type":"enum","def":"Chorus","options":["Chorus","Flanger","Phaser"]}
root\app\mod\rate: {"desc":"Rate","value":"partempo","type":"item","item_type":"module","def":"partempo"}
root\app\mod\dpth: {"desc":"Depth","value":50.0,"type":"float","min":0.0,"max":100.0,"def":50.0,"unit":"%","dec":0}
root\app\mod\mix: {"desc":"Dry-Wet","value":50.0,"type":"float","min":0.0,"max":100.0,"def":50.0,"unit":"%","dec":0}
root\app\mod\tcfolder: {"desc":"Tone and Character","value":"","type":"item","item_type":"vfolder","def":""}
root\app\mod\trfolder: {"desc":"Tremolo","value":"","type":"item","item_type":"vfolder","def":""}
root\app\mod\rate\rawdata: {"desc":"Rate","value":1.0,"type":"float","min":0.05,"max":8.0,"def":1.0,"unit":"Hz"}
root\app\mod\rate\sbdv: {"desc":"Time Subdivision","value":"1/4","type":"enum","def":"1/4","options":["4/4","2/4","1/4","Dotted 8th","1/8","1/16","Triplet"]}
root\app\mod\rate\lock: {"desc":"Lock Options","value":"Unlocked","type":"enum","def":"Unlocked","options":["Unlocked","Global","Preset"]}
root\app\mod\tcfolder\emp: {"desc":"Emphasis","value":50.0,"type":"float","min":0.0,"max":100.0,"def":50.0,"unit":"%","dec":0}
root\app\mod\tcfolder\shape: {"desc":"Shape","value":"Triang","type":"enum","def":"Triang","options":["Triang","Sin","Square"]}
root\app\mod\tcfolder\hicut: {"desc":"Hi-Cut","value":18000.0,"type":"float","min":900.0,"max":20000.0,"def":18000.0,"unit":"Hz","dec":0}
root\app\mod\tcfolder\locut: {"desc":"Lo-Cut","value":20.0,"type":"float","min":20.0,"max":1200.0,"def":20.0,"unit":"Hz","dec":0}
root\app\mod\tcfolder\sphase: {"desc":"Stereo Phase","value":0.0,"type":"float","min":0.0,"max":180.0,"def":0.0,"unit":"deg","dec":0}
root\app\mod\trfolder\on_off: {"desc":"Enable","value":"OFF","type":"enum","def":"OFF","options":["ON","OFF"]}
root\app\mod\trfolder\rate: {"desc":"Rate","value":"partempo","type":"item","item_type":"module","def":"partempo"}
root\app\mod\trfolder\dpt: {"desc":"Depth","value":25.0,"type":"float","min":0.0,"max":100.0,"def":25.0,"unit":"%","dec":0}
root\app\mod\trfolder\wave: {"desc":"Waveform","value":0.0,"type":"float","min":0.0,"max":100.0,"def":0.0,"unit":"%","dec":0}
root\app\mod\trfolder\sphase: {"desc":"Stereo Phase","value":0.0,"type":"float","min":0.0,"max":180.0,"def":0.0,"unit":"deg","dec":0}
root\app\mod\trfolder\rate\rawdata: {"desc":"Rate","value":4.0,"type":"float","min":0.7,"max":15.0,"def":4.0,"unit":"Hz"}
root\app\mod\trfolder\rate\lock: {"desc":"Lock Options","value":"Unlocked","type":"enum","def":"Unlocked","options":["Unlocked","Global","Preset"]}
root\app\mod\trfolder\rate\sbdv: {"desc":"Time Subdivision","value":"1/4","type":"enum","def":"1/4","options":["4/4","2/4","1/4","Dotted 8th","1/8","1/16","1/32","Triplet"]}
```

Two properties of this data drive the whole design:

1. **Containers are `type:"item"`** — `rate`, `tcfolder`, `trfolder` are never editable fields. `rate`'s `"partempo"` value is a module marker, not a control.
2. **Container records arrive BEFORE their children** (breadth-first), which is what makes firmware-order interleaving possible: create a group when its container record is seen, not when its first child is seen.

## File Structure

| File | Responsibility |
|---|---|
| **Create** `src/Namager.App/ViewModels/ParameterGroupViewModel.cs` | One expandable group of parameters at any depth: ordered mixed `Items`, expansion state, enable toggle, activity glyph. |
| **Delete** `src/Namager.App/ViewModels/BlockSectionViewModel.cs` | Replaced by the above. |
| **Delete** `src/Namager.App/ViewModels/SubGroupViewModel.cs` | Replaced by the above. |
| **Create** `src/Namager.App/Views/ParameterFieldView.axaml(.cs)` | One parameter row (label + slider/combo/text + reset + amp flyout), used at every depth. |
| **Create** `src/Namager.App/Views/ParameterGroupView.axaml(.cs)` | Expander + header icons + `Items`; instantiates itself for nested groups. Owns the Level block's explanation and match-volume button. |
| **Modify** `src/Namager.App/Views/ParameterEditorView.axaml(.cs)` | Toolbar, error line, scroller, `ItemsControl` over `Blocks`. Loses both copies of the field template. |
| **Modify** `src/Namager.App/ViewModels/ParameterEditorViewModel.cs` | `Blocks_InScope` gains `mod`; `LoadCoreAsync` builds the tree generically; `AllFields()` recurses; expansion defaults. |
| **Modify** `src/Namager.App/ViewModels/ParameterFieldViewModel.cs` | `Display` — unit-aware readout sharing the reset tooltip's formatter. |
| **Modify** `src/Namager.App/labels.en.json` | One entry: `root\app\mod` → `Modulation`. |
| **Create** `tests/Namager.App.Tests/ModBrowseFixture.cs` | The captured dump above, as browse records for tests. |
| **Rename** `tests/Namager.App.Tests/BlockSectionViewModelTests.cs` → `ParameterGroupViewModelTests.cs` | Ported activity-glyph tests plus new group behaviour. |
| **Modify** `tests/Namager.App.Tests/{ParameterEditorViewModelTests,AmpDetailFlyoutTests,MatchVolumeTests,LabelServiceTests}.cs` | Follow the type rename; new nesting/ordering/expansion/label coverage. |
| **Create** `docs/HARDWARE-VALIDATION-modulation.md` | On-device checklist. |
| **Modify** `docs/STATUS.md` | Retire ranked follow-up #1. |

---

## Task 1: `ParameterGroupViewModel`

Additive only — the old view models and the app keep working. This task just puts the new type in place with its tests.

**Files:**
- Create: `src/Namager.App/ViewModels/ParameterGroupViewModel.cs`
- Test: `tests/Namager.App.Tests/ParameterGroupViewModelTests.cs`

**Interfaces:**
- Consumes: `ParameterFieldViewModel` (existing — `Path`, `Text`, `Number`, `IsChangedFromDefault`).
- Produces: `ParameterGroupViewModel(string header, string path)` with `Header`, `Path`, `IsExpanded`, `ObservableCollection<object> Items`, `IEnumerable<ParameterFieldViewModel> Fields`, `IEnumerable<ParameterGroupViewModel> Groups`, `void Add(ParameterFieldViewModel)`, `void Add(ParameterGroupViewModel)`, `void InsertFirst(ParameterFieldViewModel)`, `void AttachEnableField()`, `ParameterFieldViewModel? EnableField`, `bool? Enabled`, `bool IsEqActive`, `bool ShowEqIcon`, `bool ShowLevelIcon`. Tasks 3–6 build on all of these.

- [ ] **Step 1: Write the failing tests**

Create `tests/Namager.App.Tests/ParameterGroupViewModelTests.cs`. The first six tests are the existing `BlockSectionViewModelTests` ported to the new type (`b.Fields.Add(x)` → `b.Add(x)`); the last four are new behaviour.

```csharp
using System.Globalization;
using Namager.App.ViewModels;
using Sonulab.Core.Model;
using Xunit;

public class ParameterGroupViewModelTests
{
    static ParameterFieldViewModel Field(string path, double def, double value)
    {
        var json = $@"{{""desc"":""D"",""value"":0.0,""type"":""float"",""min"":-12.0,""max"":12.0,""def"":{def.ToString(CultureInfo.InvariantCulture)}}}";
        Assert.True(NodeRecord.TryParse(path + ":" + json, out var r));
        return new ParameterFieldViewModel(NodeSchema.FromRecord(r), value.ToString(CultureInfo.InvariantCulture));
    }

    static ParameterFieldViewModel FieldNoDefault(string path, double value)
    {
        var json = @"{""desc"":""D"",""value"":0.0,""type"":""float"",""min"":-12.0,""max"":12.0}";
        Assert.True(NodeRecord.TryParse(path + ":" + json, out var r));
        return new ParameterFieldViewModel(NodeSchema.FromRecord(r), value.ToString(CultureInfo.InvariantCulture));
    }

    static ParameterFieldViewModel Enum(string path, string value)
    {
        var json = @"{""desc"":""Enable"",""value"":""ON"",""type"":""enum"",""options"":[""ON"",""OFF""]}";
        Assert.True(NodeRecord.TryParse(path + ":" + json, out var r));
        return new ParameterFieldViewModel(NodeSchema.FromRecord(r), "\"" + value + "\"");
    }

    static ParameterGroupViewModel Group(string path = @"root\app\eq", string header = "Equalizer") =>
        new(header, path);

    // ---- ported from BlockSectionViewModelTests ----

    [Fact] public void Group_with_every_field_at_its_default_is_not_active()
    {
        var b = Group(); b.ShowEqIcon = true;
        b.Add(Field(@"root\app\eq\bass", def: 0.0, value: 0.0));
        b.Add(Field(@"root\app\eq\mid", def: 0.5, value: 0.5));
        Assert.False(b.IsEqActive);
    }

    [Fact] public void A_field_away_from_its_default_makes_the_group_active()
    {
        var b = Group(); b.ShowEqIcon = true;
        b.Add(Field(@"root\app\eq\bass", def: 0.0, value: 0.0));
        b.Add(Field(@"root\app\eq\mid", def: 0.5, value: 0.9));
        Assert.True(b.IsEqActive);
    }

    [Fact] public void Nonzero_default_at_rest_is_not_active()
    {
        // The whole reason we use `def` and not literal zero: 0.5 here is FLAT, not a boost.
        var b = Group();
        b.Add(Field(@"root\app\eq\mid", def: 0.5, value: 0.5));
        Assert.False(b.IsEqActive);
    }

    [Fact] public void Missing_default_falls_back_to_zero_as_neutral()
    {
        var b = Group();
        b.Add(FieldNoDefault(@"root\app\eq\bass", value: 0.0));
        Assert.False(b.IsEqActive);
        b.Add(FieldNoDefault(@"root\app\eq\treble", value: 2.0));
        Assert.True(b.IsEqActive);
    }

    [Fact] public void Editing_a_field_recomputes_activity_and_notifies()
    {
        var b = Group();
        var bass = Field(@"root\app\eq\bass", def: 0.0, value: 0.0);
        b.Add(bass);
        Assert.False(b.IsEqActive);

        bool notified = false;
        b.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ParameterGroupViewModel.IsEqActive)) notified = true; };

        bass.Number = 4.0;
        Assert.True(b.IsEqActive);
        Assert.True(notified);

        bass.Number = 0.0;
        Assert.False(b.IsEqActive);
    }

    [Fact] public void Fields_added_before_and_after_are_both_tracked()
    {
        var b = Group();
        var late = Field(@"root\app\eq\treble", def: 0.0, value: 0.0);
        b.Add(late);
        late.Number = 3.0;
        Assert.True(b.IsEqActive);
    }

    // ---- new: one type at every depth ----

    [Fact] public void Fields_and_Groups_are_filtered_views_over_one_ordered_Items_collection()
    {
        var b = Group(@"root\app\mod", "Modulation");
        var mode = Enum(@"root\app\mod\mode", "ON");
        var rate = new ParameterGroupViewModel("Rate", @"root\app\mod\rate");
        var depth = Field(@"root\app\mod\dpth", def: 50.0, value: 50.0);
        b.Add(mode); b.Add(rate); b.Add(depth);

        // Display order is interleaved; the filtered views keep their own order.
        Assert.Equal(new object[] { mode, rate, depth }, b.Items.ToArray());
        Assert.Equal(new[] { mode, depth }, b.Fields.ToArray());
        Assert.Equal(new[] { rate }, b.Groups.ToArray());
    }

    [Fact] public void InsertFirst_puts_a_container_s_own_value_at_the_top_of_its_group()
    {
        var g = new ParameterGroupViewModel("Rate", @"root\app\mod\rate");
        g.Add(Field(@"root\app\mod\rate\rawdata", def: 1.0, value: 1.0));
        g.InsertFirst(Enum(@"root\app\mod\rate", "ON"));
        Assert.Equal(@"root\app\mod\rate", g.Fields.First().Path);
    }

    [Fact] public void AttachEnableField_adopts_this_group_s_own_on_off_and_tracks_it()
    {
        var g = new ParameterGroupViewModel("Tremolo", @"root\app\mod\trfolder");
        g.Add(Enum(@"root\app\mod\trfolder\on_off", "ON"));
        g.Add(Field(@"root\app\mod\trfolder\dpt", def: 25.0, value: 25.0));
        g.AttachEnableField();

        Assert.True(g.Enabled);
        bool raised = false;
        g.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ParameterGroupViewModel.Enabled)) raised = true; };
        g.EnableField!.Text = "OFF";
        Assert.False(g.Enabled);
        Assert.True(raised);
    }

    [Fact] public void A_group_without_an_on_off_reports_null_Enabled()
    {
        var g = new ParameterGroupViewModel("Tone and Character", @"root\app\mod\tcfolder");
        g.Add(Field(@"root\app\mod\tcfolder\emp", def: 50.0, value: 50.0));
        g.AttachEnableField();
        Assert.Null(g.Enabled);
    }

    [Fact] public void A_nested_group_s_on_off_does_not_become_the_parent_s_enable_field()
    {
        // Tremolo's on_off must not be mistaken for Modulation's — AttachEnableField only ever
        // looks at THIS group's own fields, never into nested groups.
        var parent = new ParameterGroupViewModel("Modulation", @"root\app\mod");
        var tremolo = new ParameterGroupViewModel("Tremolo", @"root\app\mod\trfolder");
        tremolo.Add(Enum(@"root\app\mod\trfolder\on_off", "ON"));
        parent.Add(tremolo);
        parent.AttachEnableField();
        Assert.Null(parent.EnableField);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterGroupViewModelTests"`
Expected: FAIL — build error, `ParameterGroupViewModel` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Namager.App/ViewModels/ParameterGroupViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Namager.App.ViewModels;

/// <summary>One expandable group of parameters — a top-level block (Amp, Modulation, Delay) OR a
/// folder nested inside one (Tremolo, Tone and Character, Rate), at any depth. Blocks and folders
/// were separate types (BlockSectionViewModel / SubGroupViewModel) until the Modulation block
/// arrived three levels deep: keeping two types meant the expansion, enable-toggle and activity
/// rules existed twice and could drift. There is one type now, so a folder behaves exactly like a
/// block by construction.</summary>
public sealed partial class ParameterGroupViewModel : ObservableObject
{
    public string Header { get; }

    /// <summary>The node path this group represents (<c>root\app\mod\trfolder</c>). The STABLE key
    /// for per-session expansion memory — headers are relabeled, paths are not — and the reason
    /// two groups both headed "Rate" are remembered independently.</summary>
    public string Path { get; }

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Fields and nested groups in ONE ordered collection, because the firmware interleaves
    /// them: `mod` publishes on_off, mode, rate(folder), dpth, mix, tcfolder, trfolder — Rate sits
    /// third, between Mode and Depth, and the editor renders it there. <see cref="Fields"/> and
    /// <see cref="Groups"/> are filtered views over this, never separate storage, so display order
    /// and logic cannot disagree.</summary>
    public ObservableCollection<object> Items { get; } = new();

    public IEnumerable<ParameterFieldViewModel> Fields => Items.OfType<ParameterFieldViewModel>();
    public IEnumerable<ParameterGroupViewModel> Groups => Items.OfType<ParameterGroupViewModel>();

    public ParameterGroupViewModel(string header, string path)
    {
        Header = header;
        Path = path;
        Items.CollectionChanged += OnItemsChanged;
    }

    public void Add(ParameterFieldViewModel field) => Items.Add(field);
    public void Add(ParameterGroupViewModel group) => Items.Add(group);

    /// <summary>Put a field at the top of this group. Used for a container node that is itself
    /// editable: its own value belongs above its children, not adrift in the parent's list.</summary>
    public void InsertFirst(ParameterFieldViewModel field) => Items.Insert(0, field);

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var f in e.NewItems?.OfType<ParameterFieldViewModel>() ?? Enumerable.Empty<ParameterFieldViewModel>())
            f.PropertyChanged += OnFieldValueChanged;
        foreach (var f in e.OldItems?.OfType<ParameterFieldViewModel>() ?? Enumerable.Empty<ParameterFieldViewModel>())
            f.PropertyChanged -= OnFieldValueChanged;
        OnPropertyChanged(nameof(Fields));
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(IsEqActive));
    }

    /// <summary>True for the `eq` block: show the equalizer glyph in the header. EQ is the one block
    /// with no `on_off` field (see <see cref="Enabled"/>), so that header slot is otherwise empty.</summary>
    [ObservableProperty] private bool _showEqIcon;

    /// <summary>True for the synthetic `Level` block: show the volume glyph in the header. That
    /// block has no `on_off` field either, so the same header slot is free.</summary>
    [ObservableProperty] private bool _showLevelIcon;

    /// <summary>True when any float field IN THIS GROUP sits away from its firmware default (where
    /// the schema omits one, 0). Drives the equalizer glyph's highlight so a non-flat EQ is visible
    /// without expanding the block. Deliberately does NOT recurse into nested groups: it is only
    /// consumed by the EQ and Level blocks, neither of which has any, and rolling nested state up
    /// would make the glyph mean something different per block. Delegates to the field's own
    /// <see cref="ParameterFieldViewModel.IsChangedFromDefault"/> — the same rule that highlights
    /// each reset button, so the glyph and its sliders always agree.</summary>
    public bool IsEqActive => Fields.Any(f => f.IsChangedFromDefault);

    private void OnFieldValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ParameterFieldViewModel.Number) or nameof(ParameterFieldViewModel.Text))
            OnPropertyChanged(nameof(IsEqActive));
    }

    private ParameterFieldViewModel? _enableField;

    /// <summary>The group's `on_off` field if it has one; drives <see cref="Enabled"/>.</summary>
    public ParameterFieldViewModel? EnableField
    {
        get => _enableField;
        set
        {
            if (_enableField is not null) _enableField.PropertyChanged -= OnEnableFieldChanged;
            _enableField = value;
            if (_enableField is not null) _enableField.PropertyChanged += OnEnableFieldChanged;
            OnPropertyChanged(nameof(Enabled));
        }
    }

    /// <summary>Adopt this group's OWN `on_off` leaf as its enable toggle. Only this group's direct
    /// fields are considered — <see cref="Fields"/> does not recurse — so Tremolo's on_off can
    /// never be mistaken for Modulation's.</summary>
    public void AttachEnableField() =>
        EnableField = Fields.FirstOrDefault(f => f.Path.EndsWith("\\on_off", StringComparison.Ordinal));

    /// <summary>True/false when the group has an on_off toggle (ON/OFF); null when it has none
    /// (e.g. eq, Tone and Character).</summary>
    public bool? Enabled => _enableField is null
        ? null
        : string.Equals(_enableField.Text, "ON", StringComparison.OrdinalIgnoreCase);

    private void OnEnableFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParameterFieldViewModel.Text)) OnPropertyChanged(nameof(Enabled));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterGroupViewModelTests"`
Expected: PASS, 11 tests.

- [ ] **Step 5: Run the full suite — nothing else may move**

Run: `dotnet test`
Expected: PASS. `BlockSectionViewModelTests` still exists and still passes; the new type is additive.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/ParameterGroupViewModel.cs tests/Namager.App.Tests/ParameterGroupViewModelTests.cs
git commit -m "feat(app): add the recursive ParameterGroupViewModel"
```

---

## Task 2: Extract the field row into `ParameterFieldView`

Pure view refactor against the OLD view models — the app must look and behave identically afterwards. This exists because `ParameterEditorView.axaml` carries two copies of the field template that have already drifted (label column 180 vs 168; the amp-detail flyout only in one), and Task 3 would otherwise have to fix recursion and duplication at once.

**Files:**
- Create: `src/Namager.App/Views/ParameterFieldView.axaml`, `src/Namager.App/Views/ParameterFieldView.axaml.cs`
- Modify: `src/Namager.App/Views/ParameterEditorView.axaml:52-104` (block-level field template) and `:118-156` (sub-group field template)

**Interfaces:**
- Consumes: `ParameterFieldViewModel` (`Label`, `Kind`, `Min`, `Max`, `Number`, `Text`, `Options`, `ShowReset`, `ResetCommand`, `ResetTooltip`, `IsChangedFromDefault`, `RefSource`); `ParameterEditorViewModel.ShowAmpDetailCommand` and `AmpDetail`, reached through `$parent[views:ParameterEditorView]`.
- Produces: `Namager.App.Views.ParameterFieldView` — a `UserControl` whose `DataContext` is one `ParameterFieldViewModel`. Task 3 renders it from `ParameterGroupView`.

- [ ] **Step 1: Create the UserControl**

Create `src/Namager.App/Views/ParameterFieldView.axaml`. This is the block-level template from `ParameterEditorView.axaml:55-101` verbatim — the richer of the two copies, so the amp-detail flyout is now available at every depth:

```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Namager.App.ViewModels" xmlns:conv="using:Namager.App.Converters"
             xmlns:views="using:Namager.App.Views"
             x:Class="Namager.App.Views.ParameterFieldView" x:DataType="vm:ParameterFieldViewModel">
  <!-- ONE field row, used at every nesting depth. There were two copies of this markup (block
       level and sub-group level) and they had already drifted apart: different label-column
       widths, and only the block copy offered the amp-detail flyout. -->
  <Grid ColumnDefinitions="180,*" Margin="4,2">
    <TextBlock Grid.Column="0" Text="{Binding Label}" VerticalAlignment="Center"
               ToolTip.Tip="{Binding Label}" TextTrimming="CharacterEllipsis"/>
    <Panel Grid.Column="1">
      <StackPanel Orientation="Horizontal" Spacing="6"
                  IsVisible="{Binding Kind, Converter={x:Static conv:Eq.Float}}">
        <Slider Minimum="{Binding Min}" Maximum="{Binding Max}" Value="{Binding Number}" Width="150"/>
        <TextBlock Text="{Binding Number, StringFormat='{}{0:F2}'}" Width="52"
                   VerticalAlignment="Center" FontFamily="Consolas,Cascadia Mono,monospace" FontSize="11"/>
        <!-- Back to the firmware default. Most are not 0, so the tooltip names the actual value
             rather than claiming "(0)". -->
        <Button Width="24" Height="24" Padding="0" VerticalAlignment="Center"
                IsVisible="{Binding ShowReset}"
                Command="{Binding ResetCommand}"
                ToolTip.Tip="{Binding ResetTooltip}">
          <PathIcon Data="{StaticResource Icon.Reset}" Width="13" Height="13"
                    Foreground="{Binding IsChangedFromDefault, Converter={x:Static conv:ActiveToBrush.Instance}}"/>
        </Button>
      </StackPanel>
      <StackPanel Orientation="Horizontal" Spacing="6"
                  IsVisible="{Binding Kind, Converter={x:Static conv:Eq.EnumOrPlist}}">
        <ComboBox ItemsSource="{Binding Options}" SelectedItem="{Binding Text}"/>
        <!-- #9: amp detail without leaving the editor. Only the amp picker gets it; loaded on
             open so browsing presets stays read-free. -->
        <Button Width="26" Height="26" Padding="0" VerticalAlignment="Center"
                ToolTip.Tip="Amp details"
                IsVisible="{Binding RefSource, Converter={x:Static conv:Eq.AmpRef}}"
                Command="{Binding $parent[views:ParameterEditorView].((vm:ParameterEditorViewModel)DataContext).ShowAmpDetailCommand}"
                CommandParameter="{Binding}">
          <PathIcon Data="{StaticResource Icon.Amp}" Width="13" Height="13"/>
          <Button.Flyout>
            <Flyout Placement="RightEdgeAlignedTop">
              <!-- Bounded + scrolling: amp metadata can carry an arbitrary number of NAM fields,
                   and an unbounded flyout would run off-screen (the same clipping class of bug
                   as #12). -->
              <ScrollViewer MaxHeight="420" MaxWidth="420" HorizontalScrollBarVisibility="Disabled">
                <views:AmpDetailView
                    DataContext="{Binding $parent[views:ParameterEditorView].((vm:ParameterEditorViewModel)DataContext).AmpDetail}"/>
              </ScrollViewer>
            </Flyout>
          </Button.Flyout>
        </Button>
      </StackPanel>
      <TextBox Text="{Binding Text}" IsVisible="{Binding Kind, Converter={x:Static conv:Eq.Str}}"/>
    </Panel>
  </Grid>
</UserControl>
```

Create `src/Namager.App/Views/ParameterFieldView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace Namager.App.Views;

public partial class ParameterFieldView : UserControl
{
    public ParameterFieldView() => InitializeComponent();
}
```

- [ ] **Step 2: Use it in both places in `ParameterEditorView.axaml`**

Replace the whole `<DataTemplate x:DataType="vm:ParameterFieldViewModel">…</DataTemplate>` body in the block-level `ItemsControl` (lines 53-103) with:

```xml
                    <ItemsControl.ItemTemplate>
                      <DataTemplate x:DataType="vm:ParameterFieldViewModel">
                        <views:ParameterFieldView/>
                      </DataTemplate>
                    </ItemsControl.ItemTemplate>
```

Do the same for the sub-group `ItemsControl` (lines 124-151). Leave everything else — the toolbar, the Level explanation block, the `SubGroups` `ItemsControl` and its `StackPanel Margin="12,4,0,0"` + header `TextBlock` — untouched.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: succeeds, no warnings. (No `.csproj` change is needed — Avalonia XAML is globbed in.)

- [ ] **Step 4: Run the full suite**

Run: `dotnet test`
Expected: PASS, unchanged count. This is a view-only refactor; no test should need editing.

- [ ] **Step 5: Eyeball the app**

Run: `dotnet run --project src/Namager.App` (VoidX-Control must be CLOSED), connect, select a preset, expand Amp and Delay.
Expected: identical to before, except sub-group rows now use the 180 px label column instead of 168 px.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/Views/ParameterFieldView.axaml src/Namager.App/Views/ParameterFieldView.axaml.cs src/Namager.App/Views/ParameterEditorView.axaml
git commit -m "refactor(app): extract the duplicated parameter row into ParameterFieldView"
```

---

## Task 3: Swap to `ParameterGroupViewModel` and render recursively

Type swap plus a recursive view, keeping the EXISTING one-level grouping logic and existing ordering. Behaviour-identical; the generic walk lands in Task 4. Splitting it this way means Task 4's diff is pure tree-building logic.

**Files:**
- Create: `src/Namager.App/Views/ParameterGroupView.axaml`, `src/Namager.App/Views/ParameterGroupView.axaml.cs`
- Modify: `src/Namager.App/Views/ParameterEditorView.axaml`, `src/Namager.App/Views/ParameterEditorView.axaml.cs`
- Modify: `src/Namager.App/ViewModels/ParameterEditorViewModel.cs`
- Delete: `src/Namager.App/ViewModels/BlockSectionViewModel.cs`, `src/Namager.App/ViewModels/SubGroupViewModel.cs`
- Delete: `tests/Namager.App.Tests/BlockSectionViewModelTests.cs` (ported in Task 1)
- Modify: `tests/Namager.App.Tests/ParameterEditorViewModelTests.cs`, `tests/Namager.App.Tests/AmpDetailFlyoutTests.cs`

**Interfaces:**
- Consumes: everything Task 1 produced.
- Produces: `ParameterEditorViewModel.Blocks` is now `ObservableCollection<ParameterGroupViewModel>`; `Namager.App.Views.ParameterGroupView` renders one group and recurses. Tasks 4–7 build on both.

- [ ] **Step 1: Update the tests that name the old types**

In `tests/Namager.App.Tests/ParameterEditorViewModelTests.cs`:

- Line 63: `var sub = Assert.Single(delay.SubGroups);` → `var sub = Assert.Single(delay.Groups);`
- Line 108: `b.Fields.Concat(b.SubGroups.SelectMany(s => s.Fields))` → `b.Fields.Concat(b.Groups.SelectMany(s => s.Fields))`
- Line 132: `vm.Blocks[0].Fields[0].Number = -6.0;` → `vm.Blocks[0].Fields.First().Number = -6.0;` (`Fields` is an `IEnumerable` view now, not a list)
- Line 201: `nameof(BlockSectionViewModel.Enabled)` → `nameof(ParameterGroupViewModel.Enabled)`
- Line 576: `b.Fields.Concat(b.SubGroups.SelectMany(s => s.Fields))` → `b.Fields.Concat(b.Groups.SelectMany(s => s.Fields))`
- Line 588: `vm.Blocks.SelectMany(b => b.SubGroups)` → `vm.Blocks.SelectMany(b => b.Groups)`
- Line 86: `var field = Assert.Single(level.Fields);` — no change needed, `Assert.Single` takes an `IEnumerable`.

In `tests/Namager.App.Tests/AmpDetailFlyoutTests.cs` line 38: `b.SubGroups.SelectMany(s => s.Fields)` → `b.Groups.SelectMany(s => s.Fields)`.

Delete `tests/Namager.App.Tests/BlockSectionViewModelTests.cs` — Task 1 ported every one of its cases.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests"`
Expected: FAIL — build error, `BlockSectionViewModel` has no member `Groups`.

- [ ] **Step 3: Switch the view model**

In `src/Namager.App/ViewModels/ParameterEditorViewModel.cs`:

Change the collection (line 95):

```csharp
    public ObservableCollection<ParameterGroupViewModel> Blocks { get; } = new();
```

In `LoadCoreAsync`, replace the Level-block construction (lines 181-196) with:

```csharp
            var levelSection = new ParameterGroupViewModel(LevelBlockHeader, levelKey) { ShowLevelIcon = true };
```

…and `levelSection.Fields.Add(levelField);` with `levelSection.Add(levelField);`.

Replace the per-block construction (lines 202-251) — same one-level logic, new types:

```csharp
            var section = new ParameterGroupViewModel(_labels.Label(prefix, DescOf(records, prefix)), prefix)
            {
                // `eq` is the only block with no on_off field, so its header icon slot is free.
                ShowEqIcon = string.Equals(block, "eq", StringComparison.OrdinalIgnoreCase),
            };
            var subgroups = new Dictionary<string, ParameterGroupViewModel>(StringComparer.Ordinal);

            foreach (var rec in records)
            {
                // Keep the existing body from `if (rec.Path != prefix && !rec.Path.StartsWith(...))`
                // down to and including `WireDirtyTracking(labeled);` EXACTLY as it stands — the
                // record filter, the schema/exposure skips, the `seg` split, the value extraction,
                // the ref-options lookup, the label, and `ShowReset`. Only the two placement
                // branches below change, and only in the type they place into.

                if (seg.Length == 4)                                     // root\app\block\leaf
                {
                    section.Add(labeled);
                }
                else                                                     // root\app\block\folder\...\leaf
                {
                    var folderPath = prefix + "\\" + seg[3];
                    if (!subgroups.TryGetValue(folderPath, out var sub))
                    {
                        sub = new ParameterGroupViewModel(_labels.Label(folderPath, DescOf(records, folderPath)), folderPath);
                        subgroups[folderPath] = sub;
                        section.Add(sub);
                    }
                    sub.Add(labeled);
                }
            }

            section.AttachEnableField();
            if (section.Items.Count > 0)
            {
                section.IsExpanded = _expansion.TryGetValue(prefix, out var exp) && exp;
                WireExpansionMemory(section, prefix);
                Blocks.Add(section);
            }
```

Change `WireExpansionMemory`'s parameter type and `nameof` target:

```csharp
    private void WireExpansionMemory(ParameterGroupViewModel section, string key) =>
        section.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ParameterGroupViewModel.IsExpanded) && s is ParameterGroupViewModel b)
                _expansion[key] = b.IsExpanded;
        };
```

Make `AllFields()` recurse to any depth (line 652):

```csharp
    /// <summary>Every field in the editor, at every nesting depth. Save, dirty tracking, the
    /// ref-option refresh and the volume-match overlay all walk this, so a field the tree builder
    /// nested three levels down must be as visible here as a top-level one.</summary>
    private IEnumerable<ParameterFieldViewModel> AllFields() => Blocks.SelectMany(FieldsOf);

    private static IEnumerable<ParameterFieldViewModel> FieldsOf(ParameterGroupViewModel g) =>
        g.Fields.Concat(g.Groups.SelectMany(FieldsOf));
```

Delete `src/Namager.App/ViewModels/BlockSectionViewModel.cs` and `src/Namager.App/ViewModels/SubGroupViewModel.cs`.

- [ ] **Step 4: Create the recursive group view**

Create `src/Namager.App/Views/ParameterGroupView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Namager.App.ViewModels" xmlns:conv="using:Namager.App.Converters"
             xmlns:views="using:Namager.App.Views"
             x:Class="Namager.App.Views.ParameterGroupView" x:DataType="vm:ParameterGroupViewModel">
  <!-- One group, at ANY depth. Nested groups are rendered by instantiating this same control —
       a self-instantiating UserControl rather than a self-referencing DataTemplate resource,
       which Avalonia resolves reliably. -->
  <Expander IsExpanded="{Binding IsExpanded}" Margin="4,2"
            HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
    <Expander.Header>
      <StackPanel Orientation="Horizontal" Spacing="8">
        <PathIcon Data="{StaticResource Icon.Power}" Width="14" Height="14"
                  VerticalAlignment="Center"
                  IsVisible="{Binding Enabled, Converter={x:Static conv:NotNull.Instance}}"
                  Foreground="{Binding Enabled, Converter={x:Static conv:EnabledToBrush.Instance}}"
                  ToolTip.Tip="{Binding Enabled, Converter={x:Static conv:EnabledToTooltip.Instance}}"/>
        <PathIcon Data="{StaticResource Icon.Equalizer}" Width="14" Height="14"
                  VerticalAlignment="Center"
                  IsVisible="{Binding ShowEqIcon}"
                  Foreground="{Binding IsEqActive, Converter={x:Static conv:ActiveToBrush.Instance}}"
                  ToolTip.Tip="Equalizer is not flat"/>
        <PathIcon Data="{StaticResource Icon.VolumeHigh}" Width="14" Height="14"
                  VerticalAlignment="Center"
                  IsVisible="{Binding ShowLevelIcon}"
                  Foreground="{Binding IsEqActive, Converter={x:Static conv:ActiveToBrush.Instance}}"
                  ToolTip.Tip="Lights up when this preset is trimmed away from 0 dB"/>
        <TextBlock Text="{Binding Header}" VerticalAlignment="Center"/>
      </StackPanel>
    </Expander.Header>
    <StackPanel>
      <!-- Fields and nested groups come out of ONE ordered collection so they interleave in
           firmware order. Two DataTemplates pick the renderer by item type. -->
      <ItemsControl ItemsSource="{Binding Items}">
        <ItemsControl.DataTemplates>
          <DataTemplate DataType="vm:ParameterFieldViewModel">
            <views:ParameterFieldView/>
          </DataTemplate>
          <DataTemplate DataType="vm:ParameterGroupViewModel">
            <views:ParameterGroupView Margin="12,0,0,0"/>
          </DataTemplate>
        </ItemsControl.DataTemplates>
      </ItemsControl>
      <!-- Only the Level block explains itself: it is the one control whose PURPOSE is not
           obvious from its label, and it was invisible before this change. -->
      <StackPanel Orientation="Horizontal" Spacing="8" Margin="4,2,4,4"
                  IsVisible="{Binding ShowLevelIcon}">
        <TextBlock Classes="section-label" TextWrapping="Wrap" MaxWidth="300"
                   Text="Trims this preset's output after every effect — use it to match loudness between presets. It doesn't change the tone."/>
        <Button Width="26" Height="26" Padding="0" VerticalAlignment="Center"
                IsEnabled="{Binding $parent[views:ParameterEditorView].((vm:ParameterEditorViewModel)DataContext).CanMatchVolume}"
                Click="OnMatchVolumeClick"
                ToolTip.Tip="Match this preset's volume to another preset">
          <PathIcon Data="{StaticResource Icon.VolumeEqual}" Width="14" Height="14"/>
        </Button>
      </StackPanel>
    </StackPanel>
  </Expander>
</UserControl>
```

Create `src/Namager.App/Views/ParameterGroupView.axaml.cs` — `OnMatchVolumeClick` moves here from `ParameterEditorView.axaml.cs` because the button it serves lives in this control now; the editor view model is reached through the ancestor view:

```csharp
using System;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Namager.App.ViewModels;

namespace Namager.App.Views;

public partial class ParameterGroupView : UserControl
{
    public ParameterGroupView() => InitializeComponent();

    /// <summary>The Level block's match-volume button. This control's DataContext is the GROUP, so
    /// the editor view model is reached through the ancestor view — the same object the button's
    /// IsEnabled binding walks to.</summary>
    private async void OnMatchVolumeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<ParameterEditorView>()?.DataContext is not ParameterEditorViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var presets = (owner.DataContext as MainWindowViewModel)?.Presets;
        if (presets is null) return;
        // async void event handler: nothing may escape to the UI thread. MatchVolumeAsync
        // already catches its own failures; this guards the picker itself.
        try { await vm.MatchVolumeAsync(() => MatchPresetDialog.ShowAsync(owner, presets.Items, vm.LoadedIndex)); }
        catch (Exception ex) { vm.ErrorMessage = $"Match failed: {ex.Message}"; }
    }
}
```

- [ ] **Step 5: Reduce `ParameterEditorView` to the shell**

In `src/Namager.App/Views/ParameterEditorView.axaml`, replace the whole `<ItemsControl ItemsSource="{Binding Blocks}">…</ItemsControl>` (lines 26-161) with:

```xml
        <ItemsControl ItemsSource="{Binding Blocks}">
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="vm:ParameterGroupViewModel">
              <views:ParameterGroupView/>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
```

Delete `OnMatchVolumeClick` from `src/Namager.App/Views/ParameterEditorView.axaml.cs` (lines 17-27) — it moved to `ParameterGroupView.axaml.cs`. Keep the constructor, `DownloadAsync`, and the `DownloadButton.Click` wiring. Remove the now-unused `using System;` only if the compiler warns; `DownloadAsync` still catches `Exception`, so it stays.

- [ ] **Step 6: Build and run the tests**

Run: `dotnet build && dotnet test`
Expected: PASS. Test count drops by the 6 deleted `BlockSectionViewModelTests` cases and rises by Task 1's 11.

- [ ] **Step 7: Eyeball the app**

Run: `dotnet run --project src/Namager.App`, connect, select a preset.
Expected: identical to Task 2's result, except sub-groups are now expanders (collapsed) rather than always-visible bold headings. Confirm the Level block still shows its explanation and that the match-volume button still opens the preset picker.

- [ ] **Step 8: Commit**

```bash
git add -A src/Namager.App tests/Namager.App.Tests
git commit -m "refactor(app): one recursive group view model and view for every nesting depth"
```

---

## Task 4: Build the tree generically, in firmware order

**Files:**
- Modify: `src/Namager.App/ViewModels/ParameterEditorViewModel.cs` (`LoadCoreAsync`)
- Test: `tests/Namager.App.Tests/ParameterEditorViewModelTests.cs`

**Interfaces:**
- Consumes: `ParameterGroupViewModel.Add/InsertFirst/AttachEnableField/Items/Path` from Task 1.
- Produces: nothing new publicly; `Blocks` now nests to arbitrary depth with groups interleaved in browse order.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Namager.App.Tests/ParameterEditorViewModelTests.cs`. Note `Vm()` uses an EMPTY label map, so headers come from the firmware `desc`.

```csharp
    // ---- generic recursive nesting (Modulation spec, Task 4) ----

    /// <summary>A delay block shaped like the real firmware: a container (`dlytime`) that arrives
    /// BEFORE its children and is itself `type:"item"`, plus a folder with a nested container.</summary>
    static FakeSonuLink NestedDev()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\delay\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\delay\\dlytime:{\"desc\":\"Time\",\"value\":\"partempo\",\"type\":\"item\",\"item_type\":\"module\"}",
            "root\\app\\delay\\fdbk:{\"desc\":\"Feedback\",\"value\":30.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}",
            "root\\app\\delay\\ddfolder:{\"desc\":\"Dual Delay\",\"value\":\"\",\"type\":\"item\",\"item_type\":\"vfolder\"}",
            "root\\app\\delay\\dlytime\\rawdata:{\"desc\":\"Time\",\"value\":300.0,\"type\":\"float\",\"min\":1.0,\"max\":2000.0,\"unit\":\"ms\"}",
            "root\\app\\delay\\dlytime\\sbdv:{\"desc\":\"Time Subdivision\",\"value\":\"1/4\",\"type\":\"enum\",\"options\":[\"1/4\",\"1/8\"]}",
            "root\\app\\delay\\ddfolder\\on_off:{\"desc\":\"Enable\",\"value\":\"OFF\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\delay\\ddfolder\\rtime:{\"desc\":\"Time R\",\"value\":\"partempo\",\"type\":\"item\",\"item_type\":\"module\"}",
            "root\\app\\delay\\ddfolder\\rtime\\rawdata:{\"desc\":\"Time R\",\"value\":300.0,\"type\":\"float\",\"min\":1.0,\"max\":2000.0,\"unit\":\"ms\"}");
        return d;
    }

    [Fact] public async Task Nesting_goes_three_levels_deep_instead_of_flattening()
    {
        var d = NestedDev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));

        var delay = vm.Blocks.Single(b => b.Path == @"root\app\delay");
        var dual = delay.Groups.Single(g => g.Path == @"root\app\delay\ddfolder");
        var rtime = dual.Groups.Single(g => g.Path == @"root\app\delay\ddfolder\rtime");
        Assert.Equal("Time R", rtime.Header);
        Assert.Equal(@"root\app\delay\ddfolder\rtime\rawdata", Assert.Single(rtime.Fields).Path);
        // The grandchild must NOT have been flattened into ddfolder alongside its own leaves.
        Assert.DoesNotContain(dual.Fields, f => f.Path.EndsWith(@"\rtime\rawdata"));
    }

    [Fact] public async Task A_container_node_produces_a_group_and_no_field_row()
    {
        var d = NestedDev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));

        var delay = vm.Blocks.Single(b => b.Path == @"root\app\delay");
        // `dlytime` is type:"item" — a container. It is a group, never a row.
        Assert.Contains(delay.Groups, g => g.Path == @"root\app\delay\dlytime");
        Assert.DoesNotContain(delay.Fields, f => f.Path == @"root\app\delay\dlytime");
    }

    [Fact] public async Task Groups_interleave_with_fields_in_firmware_browse_order()
    {
        var d = NestedDev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));

        // Firmware order is on_off, dlytime(folder), fdbk, ddfolder(folder) — the folders sit
        // where the device puts them, not appended after every field.
        var delay = vm.Blocks.Single(b => b.Path == @"root\app\delay");
        Assert.Equal(
            new[] { @"root\app\delay\on_off", @"root\app\delay\dlytime", @"root\app\delay\fdbk", @"root\app\delay\ddfolder" },
            delay.Items.Select(i => i is ParameterFieldViewModel f ? f.Path : ((ParameterGroupViewModel)i).Path).ToArray());
    }

    [Fact] public async Task A_nested_group_gets_its_own_enable_toggle()
    {
        var d = NestedDev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));

        var delay = vm.Blocks.Single(b => b.Path == @"root\app\delay");
        Assert.True(delay.Enabled);                                                  // block's own on_off
        Assert.False(delay.Groups.Single(g => g.Path.EndsWith(@"\ddfolder")).Enabled); // folder's own on_off
        Assert.Null(delay.Groups.Single(g => g.Path.EndsWith(@"\dlytime")).Enabled);   // has none
    }

    [Fact] public async Task A_folder_whose_every_leaf_is_hidden_produces_no_group()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\delay\\fdbk:{\"desc\":\"Feedback\",\"value\":30.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}",
            "root\\app\\delay\\ghost:{\"desc\":\"Ghost\",\"value\":\"\",\"type\":\"item\",\"item_type\":\"vfolder\"}",
            "root\\app\\delay\\ghost\\_st:{\"desc\":\"State\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":1.0}");
        await d.OpenAsync();
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()),
            new ParameterExposure(new[] { @"*\_st" }));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));

        var delay = vm.Blocks.Single(b => b.Path == @"root\app\delay");
        Assert.Empty(delay.Groups);
        Assert.Single(delay.Fields);
    }

    [Fact] public async Task An_editable_container_puts_its_own_value_first_inside_its_group()
    {
        // Does not occur on fw 2.5.1 — every container is type:"item". This is the guard that a
        // future firmware making one of them editable renders its value with its own children
        // rather than adrift in the parent's list.
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\delay\\dlytime:{\"desc\":\"Time\",\"value\":\"partempo\",\"type\":\"enum\",\"options\":[\"partempo\",\"ms\"]}",
            "root\\app\\delay\\dlytime\\rawdata:{\"desc\":\"Time\",\"value\":300.0,\"type\":\"float\",\"min\":1.0,\"max\":2000.0}");
        await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));

        var delay = vm.Blocks.Single(b => b.Path == @"root\app\delay");
        Assert.DoesNotContain(delay.Fields, f => f.Path == @"root\app\delay\dlytime");
        var time = delay.Groups.Single(g => g.Path == @"root\app\delay\dlytime");
        Assert.Equal(new[] { @"root\app\delay\dlytime", @"root\app\delay\dlytime\rawdata" },
                     time.Fields.Select(f => f.Path).ToArray());
    }

    [Fact] public async Task Save_writes_a_field_nested_three_levels_deep()
    {
        var d = NestedDev(); await d.OpenAsync();
        var vm = Vm(d);
        vm.PresetName = "P";
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));

        var deep = vm.Blocks.Single(b => b.Path == @"root\app\delay")
                     .Groups.Single(g => g.Path.EndsWith(@"\ddfolder"))
                     .Groups.Single(g => g.Path.EndsWith(@"\rtime"))
                     .Fields.Single();
        deep.Number = 450.0;
        Assert.True(vm.IsDirty);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains(d.CommandLog,
            c => c.StartsWith(@"write root\app\delay\ddfolder\rtime\rawdata:", StringComparison.Ordinal));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests"`
Expected: FAIL — `Nesting_goes_three_levels_deep` and `Groups_interleave` fail on the one-level builder; `A_folder_whose_every_leaf_is_hidden` and the editable-container test fail too.

- [ ] **Step 3: Replace the builder**

In `src/Namager.App/ViewModels/ParameterEditorViewModel.cs`, replace the whole per-block `foreach` body (the `subgroups` dictionary and the `seg.Length == 4` branch) with:

```csharp
        // Which paths are containers: any node that some other node hangs off. Derived from the
        // records rather than from `item_type`, so a firmware that invents a new folder flavour
        // still nests correctly.
        var parentPaths = new HashSet<string>(records.Select(r => ParentOf(r.Path)), StringComparer.Ordinal);

        foreach (var block in Blocks_InScope)
        {
            var prefix = @"root\app\" + block;
            var section = new ParameterGroupViewModel(_labels.Label(prefix, DescOf(records, prefix)), prefix)
            {
                // `eq` is the only block with no on_off field, so its header icon slot is free.
                ShowEqIcon = string.Equals(block, "eq", StringComparison.OrdinalIgnoreCase),
            };
            var byPath = new Dictionary<string, ParameterGroupViewModel>(StringComparer.Ordinal) { [prefix] = section };

            // ONE pass, in browse order, doing both jobs per record. A container's OWN record
            // always arrives before its children (mod: on_off, mode, rate, dpth, mix, tcfolder,
            // trfolder, then rate's own leaves), so creating its group right here — rather than
            // lazily on its first child — already places it where the firmware put it relative to
            // its siblings. Splitting this into a groups-first pass and a fields-second pass would
            // put every group before every field regardless of firmware order, which is not the
            // same thing: interleaving needs both jobs to advance through the SAME iteration.
            // Groups that end up empty (every leaf hidden, or a container the firmware never
            // fills) are pruned below.
            foreach (var rec in records)
            {
                if (!InBlock(rec.Path, prefix) || rec.Path == prefix) continue;

                if (parentPaths.Contains(rec.Path)) EnsureGroup(rec.Path, byPath, records);

                var schema = NodeSchema.FromRecord(rec);
                if (!EditableTypes.Contains(schema.Type)) continue;     // skip folders/containers/modules
                if (_exposure.IsHidden(rec.Path)) continue;

                var value = rec.Json.TryGetProperty("value", out var v) ? v.GetRawText() : "\"\"";
                var labeled = new ParameterFieldViewModel(schema, value,
                    schema.Ref is { Length: > 0 } fr && refOptions.TryGetValue(fr, out var opts) && opts.Count > 0
                        ? opts : null);
                labeled.Label = _labels.Label(rec.Path, schema.Desc.Length > 0 ? schema.Desc : null);
                // Reset on every float. fw 2.5.1 publishes `def` for all 86 float nodes, and only
                // the 4 EQ bands default to 0 — 58 of the rest do not (gate threshold -60 dB, comp
                // release 400 ms), so this is the only way back to factory without a manual.
                labeled.ShowReset = labeled.Kind == "float";
                WireDirtyTracking(labeled);

                // An editable node that is ALSO a container keeps its value with its own children
                // instead of in the parent's list. No fw 2.5.1 node is both (every container is
                // type:"item"); this is the guard for a firmware that changes that. The group for
                // rec.Path (if any) was just created above in this same iteration, so byPath
                // already has it.
                if (byPath.TryGetValue(rec.Path, out var own)) own.InsertFirst(labeled);
                else EnsureGroup(ParentOf(rec.Path), byPath, records).Add(labeled);
            }

            Prune(section);
            // Walk the PRUNED tree, not byPath.Values — that dictionary still holds the groups
            // Prune just detached, and wiring anything onto those leaks handlers into nothing.
            foreach (var g in SelfAndDescendants(section)) g.AttachEnableField();

            if (section.Items.Count > 0)
            {
                section.IsExpanded = _expansion.TryGetValue(prefix, out var exp) && exp;
                WireExpansionMemory(section, prefix);
                Blocks.Add(section);
            }
        }
```

Add the helpers as private members of the class:

```csharp
    private static string ParentOf(string path)
    {
        int i = path.LastIndexOf('\\');
        return i > 0 ? path[..i] : path;
    }

    private static bool InBlock(string path, string prefix) =>
        path == prefix || path.StartsWith(prefix + "\\", StringComparison.Ordinal);

    /// <summary>Find or create the group for <paramref name="path"/>, creating any missing ancestors
    /// on the way. Recursion terminates because <paramref name="byPath"/> is pre-seeded with the
    /// block's own prefix, which every path here is under.</summary>
    private ParameterGroupViewModel EnsureGroup(string path,
        Dictionary<string, ParameterGroupViewModel> byPath, IReadOnlyList<NodeRecord> records)
    {
        if (byPath.TryGetValue(path, out var existing)) return existing;
        var parent = EnsureGroup(ParentOf(path), byPath, records);
        var group = new ParameterGroupViewModel(_labels.Label(path, DescOf(records, path)), path);
        parent.Add(group);
        byPath[path] = group;
        return group;
    }

    /// <summary>This group and every group beneath it, in tree order.</summary>
    private static IEnumerable<ParameterGroupViewModel> SelfAndDescendants(ParameterGroupViewModel g) =>
        new[] { g }.Concat(g.Groups.SelectMany(SelfAndDescendants));

    /// <summary>Drop groups that ended up with nothing in them — a folder whose every leaf is
    /// blocklisted, or a container the firmware publishes but never fills. Bottom-up, so a folder
    /// holding only empty folders goes too.</summary>
    private static void Prune(ParameterGroupViewModel group)
    {
        foreach (var child in group.Groups.ToArray())
        {
            Prune(child);
            if (child.Items.Count == 0) group.Items.Remove(child);
        }
    }
```

Keep the Level-block construction above this loop exactly as Task 3 left it.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests"`
Expected: PASS, including the pre-existing `Folder_nodes_become_subgroups` and `Sub_group_floats_get_a_reset_button_too`.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/ParameterEditorViewModel.cs tests/Namager.App.Tests/ParameterEditorViewModelTests.cs
git commit -m "feat(app): build the parameter tree generically, in firmware order"
```

---

## Task 5: Put `mod` in scope

**Files:**
- Modify: `src/Namager.App/ViewModels/ParameterEditorViewModel.cs:17` (`Blocks_InScope`) and the `EstimateLoadedAsync` doc comment
- Modify: `src/Namager.App/labels.en.json`
- Create: `tests/Namager.App.Tests/ModBrowseFixture.cs`
- Test: `tests/Namager.App.Tests/ParameterEditorViewModelTests.cs`, `tests/Namager.App.Tests/LabelServiceTests.cs`, `tests/Namager.App.Tests/MatchVolumeTests.cs`

**Interfaces:**
- Consumes: the generic builder from Task 4.
- Produces: `ModBrowseFixture.Records` — a `string[]` of the captured browse records, for Task 6's tests too.

- [ ] **Step 1: Add the fixture**

Create `tests/Namager.App.Tests/ModBrowseFixture.cs` holding the 24 records from the Reference section at the top of this plan, verbatim, in device order:

```csharp
/// <summary>The real `browse root\app\mod` response from a StompStation on fw 2.5.1, captured with
/// `dotnet run --project tools/HwCheck -- --browse root\app\mod`. Used verbatim so the editor's
/// nesting, ordering and label behaviour is tested against what the device actually says rather
/// than against a hand-written idea of it.</summary>
public static class ModBrowseFixture
{
    public static string[] Records { get; } =
    {
        "root\\app\\mod:{\"desc\":\"Mod\",\"value\":\"\",\"type\":\"item\",\"item_type\":\"hfolder\",\"def\":\"\"}",
        "root\\app\\mod\\on_off:{\"desc\":\"Enable\",\"value\":\"OFF\",\"type\":\"enum\",\"def\":\"OFF\",\"options\":[\"ON\",\"OFF\"]}",
        "root\\app\\mod\\mode:{\"desc\":\"Mode\",\"value\":\"Chorus\",\"type\":\"enum\",\"def\":\"Chorus\",\"options\":[\"Chorus\",\"Flanger\",\"Phaser\"]}",
        "root\\app\\mod\\rate:{\"desc\":\"Rate\",\"value\":\"partempo\",\"type\":\"item\",\"item_type\":\"module\",\"def\":\"partempo\"}",
        "root\\app\\mod\\dpth:{\"desc\":\"Depth\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":50.0,\"unit\":\"%\",\"dec\":0}",
        "root\\app\\mod\\mix:{\"desc\":\"Dry-Wet\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":50.0,\"unit\":\"%\",\"dec\":0}",
        "root\\app\\mod\\tcfolder:{\"desc\":\"Tone and Character\",\"value\":\"\",\"type\":\"item\",\"item_type\":\"vfolder\",\"def\":\"\"}",
        "root\\app\\mod\\trfolder:{\"desc\":\"Tremolo\",\"value\":\"\",\"type\":\"item\",\"item_type\":\"vfolder\",\"def\":\"\"}",
        "root\\app\\mod\\rate\\rawdata:{\"desc\":\"Rate\",\"value\":1.0,\"type\":\"float\",\"min\":0.05,\"max\":8.0,\"def\":1.0,\"unit\":\"Hz\"}",
        "root\\app\\mod\\rate\\sbdv:{\"desc\":\"Time Subdivision\",\"value\":\"1/4\",\"type\":\"enum\",\"def\":\"1/4\",\"options\":[\"4/4\",\"2/4\",\"1/4\",\"Dotted 8th\",\"1/8\",\"1/16\",\"Triplet\"]}",
        "root\\app\\mod\\rate\\lock:{\"desc\":\"Lock Options\",\"value\":\"Unlocked\",\"type\":\"enum\",\"def\":\"Unlocked\",\"options\":[\"Unlocked\",\"Global\",\"Preset\"]}",
        "root\\app\\mod\\tcfolder\\emp:{\"desc\":\"Emphasis\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":50.0,\"unit\":\"%\",\"dec\":0}",
        "root\\app\\mod\\tcfolder\\shape:{\"desc\":\"Shape\",\"value\":\"Triang\",\"type\":\"enum\",\"def\":\"Triang\",\"options\":[\"Triang\",\"Sin\",\"Square\"]}",
        "root\\app\\mod\\tcfolder\\hicut:{\"desc\":\"Hi-Cut\",\"value\":18000.0,\"type\":\"float\",\"min\":900.0,\"max\":20000.0,\"def\":18000.0,\"unit\":\"Hz\",\"dec\":0}",
        "root\\app\\mod\\tcfolder\\locut:{\"desc\":\"Lo-Cut\",\"value\":20.0,\"type\":\"float\",\"min\":20.0,\"max\":1200.0,\"def\":20.0,\"unit\":\"Hz\",\"dec\":0}",
        "root\\app\\mod\\tcfolder\\sphase:{\"desc\":\"Stereo Phase\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":180.0,\"def\":0.0,\"unit\":\"deg\",\"dec\":0}",
        "root\\app\\mod\\trfolder\\on_off:{\"desc\":\"Enable\",\"value\":\"OFF\",\"type\":\"enum\",\"def\":\"OFF\",\"options\":[\"ON\",\"OFF\"]}",
        "root\\app\\mod\\trfolder\\rate:{\"desc\":\"Rate\",\"value\":\"partempo\",\"type\":\"item\",\"item_type\":\"module\",\"def\":\"partempo\"}",
        "root\\app\\mod\\trfolder\\dpt:{\"desc\":\"Depth\",\"value\":25.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":25.0,\"unit\":\"%\",\"dec\":0}",
        "root\\app\\mod\\trfolder\\wave:{\"desc\":\"Waveform\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":0.0,\"unit\":\"%\",\"dec\":0}",
        "root\\app\\mod\\trfolder\\sphase:{\"desc\":\"Stereo Phase\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":180.0,\"def\":0.0,\"unit\":\"deg\",\"dec\":0}",
        "root\\app\\mod\\trfolder\\rate\\rawdata:{\"desc\":\"Rate\",\"value\":4.0,\"type\":\"float\",\"min\":0.7,\"max\":15.0,\"def\":4.0,\"unit\":\"Hz\"}",
        "root\\app\\mod\\trfolder\\rate\\lock:{\"desc\":\"Lock Options\",\"value\":\"Unlocked\",\"type\":\"enum\",\"def\":\"Unlocked\",\"options\":[\"Unlocked\",\"Global\",\"Preset\"]}",
        "root\\app\\mod\\trfolder\\rate\\sbdv:{\"desc\":\"Time Subdivision\",\"value\":\"1/4\",\"type\":\"enum\",\"def\":\"1/4\",\"options\":[\"4/4\",\"2/4\",\"1/4\",\"Dotted 8th\",\"1/8\",\"1/16\",\"1/32\",\"Triplet\"]}",
    };

    /// <summary>The same records with `trfolder\on_off` (Tremolo's own enable) flipped to ON, for
    /// the auto-open cases. `mod\on_off` is left OFF — a test that needs the BLOCK on flips it
    /// itself, so the two switches never move together by accident.</summary>
    public static string[] WithTremoloOn() =>
        Records.Select(r => r.StartsWith("root\\app\\mod\\trfolder\\on_off:", StringComparison.Ordinal)
                                ? r.Replace("\"value\":\"OFF\"", "\"value\":\"ON\"")
                                : r)
               .ToArray();
}
```

- [ ] **Step 2: Write the failing tests**

Append to `tests/Namager.App.Tests/ParameterEditorViewModelTests.cs`:

```csharp
    // ---- Modulation block (the real firmware tree) ----

    static ParameterEditorViewModel ModVm(FakeSonuLink d) =>
        new(new SonuClient(d),
            new LabelService(new Dictionary<string, string> { [@"root\app\mod"] = "Modulation" }),
            new ParameterExposure(new[] { @"*\_st" }));

    static async Task<ParameterGroupViewModel> LoadModAsync(string[] records)
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app", records);
        await d.OpenAsync();
        var vm = ModVm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        return vm.Blocks.Single(b => b.Path == @"root\app\mod");
    }

    [Fact] public async Task Mod_renders_between_ir_and_delay()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app", ModBrowseFixture.Records
            .Append("root\\app\\ir\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}")
            .Append("root\\app\\delay\\fdbk:{\"desc\":\"Feedback\",\"value\":30.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}")
            .ToArray());
        await d.OpenAsync();
        var vm = ModVm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        Assert.Equal(new[] { @"root\app\ir", @"root\app\mod", @"root\app\delay" },
                     vm.Blocks.Select(b => b.Path).ToArray());
    }

    [Fact] public async Task Mod_block_header_comes_from_the_label_map_not_the_firmware_desc()
    {
        var mod = await LoadModAsync(ModBrowseFixture.Records);
        Assert.Equal("Modulation", mod.Header);      // firmware desc is the unhelpful "Mod"
    }

    [Fact] public async Task Mod_renders_rate_third_with_its_folders_in_firmware_order()
    {
        var mod = await LoadModAsync(ModBrowseFixture.Records);
        Assert.Equal(
            new[] { @"root\app\mod\on_off", @"root\app\mod\mode", @"root\app\mod\rate",
                    @"root\app\mod\dpth", @"root\app\mod\mix",
                    @"root\app\mod\tcfolder", @"root\app\mod\trfolder" },
            mod.Items.Select(i => i is ParameterFieldViewModel f ? f.Path : ((ParameterGroupViewModel)i).Path).ToArray());
    }

    [Fact] public async Task Mod_folder_headers_come_from_the_firmware_desc()
    {
        var mod = await LoadModAsync(ModBrowseFixture.Records);
        Assert.Equal(new[] { "Rate", "Tone and Character", "Tremolo" },
                     mod.Groups.Select(g => g.Header).ToArray());
    }

    [Fact] public async Task Tremolo_holds_its_own_Rate_group_three_levels_down()
    {
        var mod = await LoadModAsync(ModBrowseFixture.Records);
        var tremolo = mod.Groups.Single(g => g.Path == @"root\app\mod\trfolder");
        var rate = tremolo.Groups.Single(g => g.Path == @"root\app\mod\trfolder\rate");
        Assert.Equal(new[] { @"root\app\mod\trfolder\rate\rawdata",
                             @"root\app\mod\trfolder\rate\lock",
                             @"root\app\mod\trfolder\rate\sbdv" },
                     rate.Fields.Select(f => f.Path).ToArray());
        // Rate is Tremolo's SECOND item, after its Enable — firmware order again.
        Assert.Equal(@"root\app\mod\trfolder\rate",
                     ((ParameterGroupViewModel)tremolo.Items[1]).Path);
    }

    [Fact] public async Task The_two_Rate_groups_are_distinct_nodes_despite_the_same_header()
    {
        var mod = await LoadModAsync(ModBrowseFixture.Records);
        var top = mod.Groups.Single(g => g.Path == @"root\app\mod\rate");
        var nested = mod.Groups.Single(g => g.Path == @"root\app\mod\trfolder")
                       .Groups.Single(g => g.Path == @"root\app\mod\trfolder\rate");
        Assert.Equal(top.Header, nested.Header);
        Assert.NotEqual(top.Path, nested.Path);
        // Their rawdata ranges differ (0.05–8 Hz vs 0.7–15 Hz) — proof they are not the same node.
        Assert.Equal(8.0, top.Fields.First(f => f.Path.EndsWith(@"\rawdata")).Max);
        Assert.Equal(15.0, nested.Fields.First(f => f.Path.EndsWith(@"\rawdata")).Max);
    }

    [Fact] public async Task The_rate_module_node_is_never_a_field()
    {
        var mod = await LoadModAsync(ModBrowseFixture.Records);
        var all = vmAllPaths(mod);
        Assert.DoesNotContain(@"root\app\mod\rate", all);
        Assert.DoesNotContain(@"root\app\mod\trfolder\rate", all);
        Assert.Contains(@"root\app\mod\rate\rawdata", all);

        static IEnumerable<string> vmAllPaths(ParameterGroupViewModel g) =>
            g.Fields.Select(f => f.Path).Concat(g.Groups.SelectMany(vmAllPaths));
    }
```

Append to `tests/Namager.App.Tests/LabelServiceTests.cs`:

```csharp
    [Fact] public void Embedded_labels_rename_the_mod_block_to_Modulation()
    {
        // The firmware calls it "Mod". Every other node under it self-describes correctly, so this
        // is the only override the Modulation block needs.
        Assert.Equal("Modulation", LabelService.Default.Label(@"root\app\mod", "Mod"));
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests|FullyQualifiedName~LabelServiceTests"`
Expected: FAIL — no `mod` block is built (`Single` throws), and the label lookup returns "Mod".

- [ ] **Step 4: Put `mod` in scope and label it**

`src/Namager.App/ViewModels/ParameterEditorViewModel.cs:17` — insert `"mod"` between `"ir"` and `"delay"`, matching the pedal's own signal order:

```csharp
    public static IReadOnlyList<string> Blocks_InScope { get; } = new[] { "gate", "exp", "comp", "amp", "eq", "ir", "mod", "delay", "reverb" };
```

`src/Namager.App/labels.en.json` — add the one entry (keep the file's existing key order, alphabetical by block):

```json
{
  "root\\app\\exp": "Expression",
  "root\\app\\comp": "Compressor",
  "root\\app\\eq": "Equalizer",
  "root\\app\\ir": "Impulse Response",
  "root\\app\\delay\\tcfolder": "Tone and Character",
  "root\\app\\delay\\modfolder": "Modulation",
  "root\\app\\delay\\ddfolder": "Dual Delay",
  "root\\app\\mod": "Modulation",
  "root\\app\\mod\\tcfolder": "Tone and Character",
  "root\\app\\ir\\ir2": "Stereo IR",
  "root\\app\\output\\pst\\level": "Preset Level"
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests|FullyQualifiedName~LabelServiceTests"`
Expected: PASS.

- [ ] **Step 6: Repair the volume-match test's dead premise**

`MatchVolumeTests` documents `mod` as permanently out of the editor's reach. That is now false, and its `A_mod_block_on_the_loaded_preset_surfaces_the_caveat` test would keep passing only because its fake's browse response happens to contain no `mod` node — passing for the wrong reason.

In `tests/Namager.App.Tests/MatchVolumeTests.cs`, replace the comment above `PstWithModOn()` (lines 69-70) with:

```csharp
    // A .pst with its Modulation block ON. Dev()'s browse response carries no `mod` node, so the
    // editor builds no field for it even though `mod` IS in Blocks_InScope — exactly the shape
    // this guards: LevelModel.InputPaths is not guaranteed to be a subset of what the editor
    // exposes (a blocklisted leaf, a firmware that omits a node), and LevelModel.IsOff reads an
    // absent path as OFF. Only the .pst carries the truth here.
```

Replace the comment inside `A_mod_block_on_the_loaded_preset_surfaces_the_caveat` (lines 315-320) with:

```csharp
        // Before the fix, EstimateLoadedAsync built its dictionary from AllFields() alone, so a
        // path the editor does not expose was silently absent — and absent reads as OFF. The
        // TARGET side (built from the target's own .pst) saw the same flag correctly, which made
        // the caveat direction-dependent. It must surface regardless of which side carries it.
```

Add a test proving the other half — that a live editor value now overrides the stored `.pst` at a
`mod` path, which only became reachable when `mod` entered scope:

```csharp
    [Fact]
    public async Task A_live_mod_edit_overrides_the_stored_pst_value()
    {
        // `mod` is in Blocks_InScope now, so the editor holds a live mod\on_off. The .pst says ON;
        // the user has switched it OFF and not saved. The estimate must describe what the user is
        // hearing, so the caveat must NOT appear.
        var d = Dev();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\amp:{\"desc\":\"Amp\",\"value\":\"TestAmp\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0}",
            "root\\app\\amp\\vol:{\"desc\":\"Volume\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":50.0}",
            "root\\app\\eq\\level:{\"desc\":\"Level\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0}",
            "root\\app\\mod\\on_off:{\"desc\":\"Enable\",\"value\":\"OFF\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\output\\pst\\level:{\"desc\":\"Preset Level\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0,\"unit\":\"dB\",\"dec\":1}");
        await d.OpenAsync();
        var status = new FakeStatusService();
        var loadedPst = PstWithModOn();
        var targetPst = TargetPst(eqLevel: 6.0);
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()), ParameterExposure.Default,
            status: status,
            repo: new Sonulab.Core.Services.DeviceRepository(new SonuClient(d)),
            readAmpBlob: (_, _) => Task.FromResult(FlatAmpSlot()),
            readIrBlob: (_, _) => Task.FromResult<byte[]?>(null),
            readPresetDoc: (index, _) => Task.FromResult(index == 0 ? loadedPst : targetPst));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.DoesNotContain(status.Succeeded, m => m.Contains("Modulation", StringComparison.Ordinal));
    }
```

- [ ] **Step 7: Rewrite the `EstimateLoadedAsync` doc comment**

In `src/Namager.App/ViewModels/ParameterEditorViewModel.cs`, the summary on `EstimateLoadedAsync` argues from "`mod` is not in scope", which no longer holds. The `.pst`-base design is unchanged; only its justification moves. Replace the first paragraph with:

```csharp
    /// <summary>Estimate the preset currently in the editor. The base layer is the loaded slot's
    /// OWN `.pst` — read via <see cref="Sonulab.Distill.LevelModel.InputPaths"/>, exactly like
    /// <see cref="EstimateSlotAsync"/> builds the target's — rather than <c>AllFields()</c>.
    /// `InputPaths` is not guaranteed to be a subset of what the editor exposes: a leaf can be
    /// blocklisted in hidden-params.json, and a firmware revision can omit a node the model reads.
    /// Any such path would be silently ABSENT from an AllFields()-only dictionary, and
    /// LevelModel.IsOff treats an absent path as OFF regardless of the device's real value. That
    /// made the caveat direction-dependent: a preset with chorus on was flagged only when it was
    /// the TARGET, never when it was the one loaded. (`mod` itself was the original case, before
    /// it joined Blocks_InScope; the hazard is structural, not specific to that block.) Reading
    /// both sides through the same InputPaths contract keeps the caveat symmetric.
```

Leave the second paragraph (the live-field overlay) as it stands — it is still accurate and now
covers `mod` too.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add -A src/Namager.App tests/Namager.App.Tests
git commit -m "feat(app): make the Modulation block editable, between IR and Delay"
```

---

## Task 6: Auto-open active nested groups

**Files:**
- Modify: `src/Namager.App/ViewModels/ParameterEditorViewModel.cs` (`LoadCoreAsync`)
- Test: `tests/Namager.App.Tests/ParameterEditorViewModelTests.cs`

**Interfaces:**
- Consumes: `ParameterGroupViewModel.Enabled`, `AttachEnableField()`, `Path`, `IsExpanded`.
- Produces: no new API.

- [ ] **Step 1: Write the failing tests**

```csharp
    // ---- expansion: collapsed by default, open what's active ----

    [Fact] public async Task A_nested_group_whose_on_off_is_ON_opens_itself()
    {
        var mod = await LoadModAsync(ModBrowseFixture.WithTremoloOn());
        var tremolo = mod.Groups.Single(g => g.Path == @"root\app\mod\trfolder");
        Assert.True(tremolo.IsExpanded);
    }

    [Fact] public async Task A_nested_group_whose_on_off_is_OFF_stays_collapsed()
    {
        var mod = await LoadModAsync(ModBrowseFixture.Records);
        Assert.False(mod.Groups.Single(g => g.Path == @"root\app\mod\trfolder").IsExpanded);
    }

    [Fact] public async Task A_nested_group_with_no_on_off_stays_collapsed()
    {
        var mod = await LoadModAsync(ModBrowseFixture.Records);
        Assert.False(mod.Groups.Single(g => g.Path == @"root\app\mod\tcfolder").IsExpanded);
        Assert.False(mod.Groups.Single(g => g.Path == @"root\app\mod\rate").IsExpanded);
    }

    [Fact] public async Task A_top_level_block_never_auto_opens_however_active_it_is()
    {
        // Amp, IR, Delay and Reverb are all ON in a typical preset — auto-opening blocks would
        // expand the whole editor on every preset load. The rule is for nested groups only.
        var mod = await LoadModAsync(ModBrowseFixture.WithTremoloOn()
            .Select(r => r.StartsWith("root\\app\\mod\\on_off:", StringComparison.Ordinal)
                             ? r.Replace("\"value\":\"OFF\"", "\"value\":\"ON\"") : r).ToArray());
        Assert.True(mod.Enabled);
        Assert.False(mod.IsExpanded);
    }

    [Fact] public async Task Collapsing_an_auto_opened_group_sticks_across_a_preset_switch()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app", ModBrowseFixture.WithTremoloOn());
        await d.OpenAsync();
        var vm = ModVm(d);

        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P0"));
        var tremolo = vm.Blocks.Single(b => b.Path == @"root\app\mod")
                        .Groups.Single(g => g.Path == @"root\app\mod\trfolder");
        Assert.True(tremolo.IsExpanded);
        tremolo.IsExpanded = false;                     // the user disagrees

        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(1, "P1"));
        var again = vm.Blocks.Single(b => b.Path == @"root\app\mod")
                      .Groups.Single(g => g.Path == @"root\app\mod\trfolder");
        Assert.NotSame(tremolo, again);                 // rebuilt, not reused
        Assert.False(again.IsExpanded);                 // memory beats the auto-open rule
    }

    [Fact] public async Task Expansion_memory_keeps_the_two_Rate_groups_independent()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app", ModBrowseFixture.Records);
        await d.OpenAsync();
        var vm = ModVm(d);

        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P0"));
        vm.Blocks.Single(b => b.Path == @"root\app\mod")
          .Groups.Single(g => g.Path == @"root\app\mod\rate").IsExpanded = true;

        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(1, "P1"));
        var mod = vm.Blocks.Single(b => b.Path == @"root\app\mod");
        Assert.True(mod.Groups.Single(g => g.Path == @"root\app\mod\rate").IsExpanded);
        Assert.False(mod.Groups.Single(g => g.Path == @"root\app\mod\trfolder")
                       .Groups.Single(g => g.Path == @"root\app\mod\trfolder\rate").IsExpanded);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests"`
Expected: FAIL — `A_nested_group_whose_on_off_is_ON_opens_itself` and the two memory tests fail (nested groups have no expansion handling at all yet).

- [ ] **Step 3: Apply expansion to every group**

In `LoadCoreAsync`, extend the `AttachEnableField` walk from Task 4 to set expansion at the same time:

```csharp
            Prune(section);
            foreach (var g in SelfAndDescendants(section))
            {
                g.AttachEnableField();
                // The section itself is handled below — a BLOCK never auto-opens (see ApplyExpansion).
                if (!ReferenceEquals(g, section)) ApplyExpansion(g, autoOpen: g.Enabled == true);
            }
```

…and add the helper next to `WireExpansionMemory`:

```csharp
    /// <summary>Set a group's initial expansion and remember every later toggle. Per-session memory
    /// always wins: <paramref name="autoOpen"/> is only the starting point for a group the user has
    /// not yet had an opinion about.
    ///
    /// Nested groups pass autoOpen = "my own on_off is ON", so opening Modulation shows an engaged
    /// Tremolo without a second click. Top-level BLOCKS deliberately do not — Amp, IR, Delay and
    /// Reverb are all on in a typical preset, so the same rule there would expand the whole editor
    /// on every preset load.</summary>
    private void ApplyExpansion(ParameterGroupViewModel group, bool autoOpen)
    {
        group.IsExpanded = _expansion.TryGetValue(group.Path, out var remembered) ? remembered : autoOpen;
        WireExpansionMemory(group, group.Path);
    }
```

Route the two existing call sites through it so there is one expansion rule in the file. The block:

```csharp
            if (section.Items.Count > 0)
            {
                ApplyExpansion(section, autoOpen: false);
                Blocks.Add(section);
            }
```

The Level block (replacing its `IsExpanded` line and `WireExpansionMemory` call):

```csharp
            // Expanded by default — unlike every other block. This is the headline control and
            // was invisible before; a collapsed default would leave it just as hard to find.
            // The per-session memory still wins once the user has collapsed it.
            ApplyExpansion(levelSection, autoOpen: true);
```

Note the ordering requirement: `ApplyExpansion` reads `Enabled`, so `AttachEnableField()` must run
first — which is why both live in the same loop above.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests"`
Expected: PASS, including the pre-existing `Level_block_is_expanded_by_default`.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/ParameterEditorViewModel.cs tests/Namager.App.Tests/ParameterEditorViewModelTests.cs
git commit -m "feat(app): open a nested group whose own on_off is on"
```

---

## Task 7: Unit-aware value readout

**Files:**
- Modify: `src/Namager.App/ViewModels/ParameterFieldViewModel.cs:53-68`
- Modify: `src/Namager.App/Views/ParameterFieldView.axaml`
- Test: `tests/Namager.App.Tests/ParameterFieldViewModelTests.cs` (add to the existing file)

**Interfaces:**
- Consumes: `NodeSchema.Unit`, `NodeSchema.Dec` (already parsed).
- Produces: `ParameterFieldViewModel.Display` — the formatted current value, re-raised whenever `Number` changes.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Namager.App.Tests/ParameterFieldViewModelTests.cs` (create the file with the same `using`s as `ParameterGroupViewModelTests` if it does not exist):

```csharp
    static ParameterFieldViewModel FromJson(string path, string json, string value)
    {
        Assert.True(NodeRecord.TryParse(path + ":" + json, out var r));
        return new ParameterFieldViewModel(NodeSchema.FromRecord(r), value);
    }

    [Fact] public void Display_uses_the_firmware_unit_and_decimal_hints()
    {
        var hicut = FromJson(@"root\app\mod\tcfolder\hicut",
            @"{""desc"":""Hi-Cut"",""value"":18000.0,""type"":""float"",""min"":900.0,""max"":20000.0,""def"":18000.0,""unit"":""Hz"",""dec"":0}",
            "18000.0");
        Assert.Equal("18000 Hz", hicut.Display);
    }

    [Fact] public void Display_puts_no_space_before_a_percent_sign()
    {
        var depth = FromJson(@"root\app\mod\dpth",
            @"{""desc"":""Depth"",""value"":50.0,""type"":""float"",""min"":0.0,""max"":100.0,""def"":50.0,""unit"":""%"",""dec"":0}",
            "50.0");
        Assert.Equal("50%", depth.Display);
    }

    [Fact] public void Display_falls_back_to_two_significant_decimals_when_dec_is_absent()
    {
        // mod\rate\rawdata publishes a unit but no `dec`.
        var rate = FromJson(@"root\app\mod\rate\rawdata",
            @"{""desc"":""Rate"",""value"":1.0,""type"":""float"",""min"":0.05,""max"":8.0,""def"":1.0,""unit"":""Hz""}",
            "1.25");
        Assert.Equal("1.25 Hz", rate.Display);
    }

    [Fact] public void Display_omits_the_unit_when_the_schema_has_none()
    {
        var plain = FromJson(@"root\app\eq\bass",
            @"{""desc"":""Bass"",""value"":0.0,""type"":""float"",""min"":-12.0,""max"":12.0,""def"":0.0}",
            "3.5");
        Assert.Equal("3.5", plain.Display);
    }

    [Fact] public void Display_tracks_the_value_and_notifies()
    {
        var depth = FromJson(@"root\app\mod\dpth",
            @"{""desc"":""Depth"",""value"":50.0,""type"":""float"",""min"":0.0,""max"":100.0,""def"":50.0,""unit"":""%"",""dec"":0}",
            "50.0");
        bool raised = false;
        depth.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ParameterFieldViewModel.Display)) raised = true; };
        depth.Number = 75.0;
        Assert.Equal("75%", depth.Display);
        Assert.True(raised);
    }

    [Fact] public void The_reset_tooltip_and_the_readout_use_the_same_formatter()
    {
        var hicut = FromJson(@"root\app\mod\tcfolder\hicut",
            @"{""desc"":""Hi-Cut"",""value"":18000.0,""type"":""float"",""min"":900.0,""max"":20000.0,""def"":18000.0,""unit"":""Hz"",""dec"":0}",
            "18000.0");
        Assert.Equal($"Reset to default ({hicut.Display})", hicut.ResetTooltip);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterFieldViewModelTests"`
Expected: FAIL — `ParameterFieldViewModel` has no `Display`.

- [ ] **Step 3: Share the formatter**

In `src/Namager.App/ViewModels/ParameterFieldViewModel.cs`, replace `FormatDefault()` (lines 55-68) with a shared formatter, and add `Display`:

```csharp
    /// <summary>Names the actual default, e.g. "Reset to default (400 ms)". A fixed "(0)" would be
    /// a lie on two thirds of the pedal's sliders.</summary>
    public string ResetTooltip => $"Reset to default ({Format(Default ?? 0.0)})";

    /// <summary>The current value as the device would print it — the slider readout. Shares its
    /// formatter with <see cref="ResetTooltip"/> so "18000 Hz" and "Reset to default (18000 Hz)"
    /// can never disagree about units or precision.</summary>
    public string Display => Format(Number);

    private string Format(double v)
    {
        // "0.##" rather than a fixed precision when the schema omits `dec`: shows 5 as "5", not "5.00".
        string num = Dec is int d and >= 0
            ? v.ToString("F" + d, CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);
        return Unit switch
        {
            null or "" => num,
            "%" => num + "%",          // "50%", not "50 %"
            _ => $"{num} {Unit}",
        };
    }
```

Extend the EXISTING `OnNumberChanged` partial (line 49) — CommunityToolkit allows only one
definition per property, so add to it rather than declaring a second:

```csharp
    partial void OnNumberChanged(double value)
    {
        OnPropertyChanged(nameof(IsChangedFromDefault));
        OnPropertyChanged(nameof(Display));
    }
```

Update the `Unit` and `Dec` doc comments (lines 23-28) — they claim "used by the reset tooltip", now
also the readout:

```csharp
    /// <summary>The node's display unit ("dB", "ms", "%", "Hz", "deg"), or null. Used by the reset
    /// tooltip and the slider readout.</summary>
    public string? Unit { get; }

    /// <summary>Decimal places the firmware suggests for this node, or null. Used by the reset
    /// tooltip and the slider readout so a value reads the way the device would print it.</summary>
    public int? Dec { get; }
```

- [ ] **Step 4: Bind it**

In `src/Namager.App/Views/ParameterFieldView.axaml`, replace the readout `TextBlock`:

```xml
        <TextBlock Text="{Binding Display}" Width="64"
                   VerticalAlignment="Center" FontFamily="Consolas,Cascadia Mono,monospace" FontSize="11"/>
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~ParameterFieldViewModelTests"`
Expected: PASS.

- [ ] **Step 6: Run the full suite and eyeball**

Run: `dotnet test && dotnet run --project src/Namager.App`
Expected: tests PASS; in the app, Modulation's Hi-Cut reads `18000 Hz`, Depth `50%`, Stereo Phase `0 deg`, and Delay's time `300 ms`. Confirm the wider column has not pushed the reset button out of view at the narrowest usable window width.

- [ ] **Step 7: Commit**

```bash
git add src/Namager.App/ViewModels/ParameterFieldViewModel.cs src/Namager.App/Views/ParameterFieldView.axaml tests/Namager.App.Tests/ParameterFieldViewModelTests.cs
git commit -m "feat(app): show the firmware's unit and precision on the slider readout"
```

---

## Task 8: Hardware validation and status

**Files:**
- Create: `docs/HARDWARE-VALIDATION-modulation.md`
- Modify: `docs/STATUS.md`

- [ ] **Step 1: Write the validation checklist**

Create `docs/HARDWARE-VALIDATION-modulation.md`:

```markdown
# Hardware validation — Modulation block + recursive nesting

Device: StompStation "AMP Station", fw 2.5.1, USB (VoidX-Control CLOSED).
**Back up first:** the app's backup button, or `dotnet run --project tools/HwCheck` for a read-only
sanity check. Backups land in `docs/backups/` (gitignored).

## Structure

- [ ] Modulation appears between Impulse Response and Delay.
- [ ] Its header carries the power glyph, lit per `mod\on_off`.
- [ ] Expanding it shows, in order: Enable, Mode, ▸ Rate, Depth, Dry-Wet, ▸ Tone and Character, ▸ Tremolo.
- [ ] Tremolo has its own power glyph and its own ▸ Rate inside it.
- [ ] Rate (both of them) holds Rate, Time Subdivision, Lock Options — and nothing else.
- [ ] The two Rate groups expand independently; collapsing one does not move the other.
- [ ] Select a preset with Tremolo ON: Tremolo is already expanded when you open Modulation.
      Select one with it OFF: collapsed.
- [ ] Collapse an auto-opened Tremolo, switch presets, return — it stays collapsed.

## Round-trip (one parameter at a time, Save between each)

For each of `mode`, `dpth`, `mix`, `rate\rawdata`, `rate\sbdv`, `rate\lock`, `tcfolder\emp`,
`tcfolder\shape`, `tcfolder\hicut`, `tcfolder\locut`, `tcfolder\sphase`, `trfolder\on_off`,
`trfolder\dpt`, `trfolder\wave`, `trfolder\sphase`, `trfolder\rate\rawdata`, `trfolder\rate\sbdv`,
`trfolder\rate\lock`:

- [ ] Change it, Save, select another preset, come back — the value persisted.
- [ ] Confirm against the device: `dotnet run --project tools/HwCheck -- --browse root\app\mod`.

## Audible

- [ ] `mod\on_off` ON with Mode = Chorus: Depth and Dry-Wet audibly change the effect.
- [ ] Mode = Flanger and Mode = Phaser each sound distinct.
- [ ] Rate `rawdata` changes the sweep speed; `sbdv` does so in tempo-synced steps.
- [ ] Tremolo ON: its Depth, Waveform and its own Rate act independently of the parent block.
- [ ] Rate `lock` = Global / Preset behaves sanely against the pedal tempo (observe only — this
      cycle does not design around tempo lock).

## Readout

- [ ] Hi-Cut reads `18000 Hz`, not `18000.00`. Depth reads `50%`. Stereo Phase reads `0 deg`.
- [ ] Delay time reads `300 ms`; gate threshold reads `-60 dB`.

## No regressions elsewhere

- [ ] Delay renders with Time / Tone and Character / Modulation / Dual Delay as expanders, Time
      second, and every field it had before is still present and still saves.
- [ ] Dual Delay's own Time R group is nested inside it, not flattened alongside its leaves.
- [ ] Expression's Wah and Volume folders still render and save.
- [ ] The Level block is still first, still expanded, still explains itself, and match-volume still
      opens the preset picker and applies a proposal.
- [ ] The amp picker's detail flyout still opens from the Amp block.
```

- [ ] **Step 2: Retire the follow-up in STATUS.md**

In `docs/STATUS.md`, delete ranked follow-up #1 (the `mod`-not-editable entry, lines 49-60) and
renumber the follow-ups after it. Add a line to the shipped section recording that the Modulation
block is now editable and that parameter nesting is recursive.

- [ ] **Step 3: Run the app once more end to end**

Run: `dotnet run --project src/Namager.App`
Expected: work through the checklist above against the real pedal. Record any deviation in the doc
rather than silently fixing it.

- [ ] **Step 4: Commit**

```bash
git add docs/HARDWARE-VALIDATION-modulation.md docs/STATUS.md
git commit -m "docs: Modulation hardware checklist; retire the mod-not-editable follow-up"
```
