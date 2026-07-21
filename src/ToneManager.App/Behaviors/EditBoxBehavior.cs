using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ToneManager.App.Behaviors;

/// <summary>
/// Attached property for the in-place rename box: when <c>FocusOnVisible</c> becomes true (bound to the
/// row's IsEditing), the TextBox is focused and its text selected once layout settles.
/// </summary>
public static class EditBoxBehavior
{
    public static readonly AttachedProperty<bool> FocusOnVisibleProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("FocusOnVisible", typeof(EditBoxBehavior));

    public static void SetFocusOnVisible(TextBox o, bool value) => o.SetValue(FocusOnVisibleProperty, value);
    public static bool GetFocusOnVisible(TextBox o) => o.GetValue(FocusOnVisibleProperty);

    static EditBoxBehavior()
    {
        FocusOnVisibleProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            if (e.GetNewValue<bool>())
                Dispatcher.UIThread.Post(() =>
                {
                    if (box.IsVisible) { box.Focus(); box.SelectAll(); }
                }, DispatcherPriority.Loaded);
        });
    }
}
