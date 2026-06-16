using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sonulab.App.Behaviors;
using Sonulab.App.ViewModels;

namespace Sonulab.App.Views;

public partial class PresetListView : UserControl
{
    // In-process string format for carrying the drag source index as a string.
    private static readonly DataFormat<string> PresetIndexFormat =
        DataFormat.CreateInProcessFormat<string>("preset-index");

    private int _dragFrom = -1;

    public PresetListView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => PresetDropIndicator.Hide());
    }

    private async void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Ignore presses on the per-row reorder buttons so they fire their command, not a drag.
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) != null) return;

        if (sender is Control c && c.DataContext is PresetItemViewModel item && !item.IsEmpty)
        {
            _dragFrom = item.Index;
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(PresetIndexFormat, item.Index.ToString()));
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
    }

    private int IndexAt(DragEventArgs e)
    {
        var list = this.FindControl<ListBox>("PresetList")!;
        for (int i = 0; i < list.ItemCount; i++)
            if (list.ContainerFromIndex(i) is Control row)
            {
                var p = e.GetPosition(row);
                if (p.Y >= 0 && p.Y <= row.Bounds.Height)
                    return p.Y < row.Bounds.Height / 2 ? i : i + 1;   // before/after this row
            }
        return list.ItemCount;   // past the end
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(PresetIndexFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        PresetDropIndicator.Show(this.FindControl<ListBox>("PresetList")!, IndexAt(e));
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        PresetDropIndicator.Hide();
        if (_dragFrom < 0 || DataContext is not PresetListViewModel vm) return;
        int insert = IndexAt(e);
        int to = insert > _dragFrom ? insert - 1 : insert;     // account for removal shift
        if (to != _dragFrom && vm.MoveToCommand.CanExecute((_dragFrom, to)))
            vm.MoveToCommand.Execute((_dragFrom, to));
        _dragFrom = -1;
        e.Handled = true;
    }
}
