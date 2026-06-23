using System.Windows;
using System.Windows.Controls;

namespace WinDeploy.App.Behaviors;

/// <summary>Attached behavior that tidies a menu's separators when it opens: a separator is hidden when it has
/// no visible item to separate — i.e. it's leading, trailing, or directly follows another separator (which
/// happens whenever conditionally-visible items around it are all hidden). Recomputed on every open so it
/// adapts to items whose <see cref="UIElement.Visibility"/> changes with state. Enabled via the
/// ThemedContextMenu style, so it applies to every themed context menu.</summary>
public static class MenuTidy
{
    public static readonly DependencyProperty AutoSeparatorsProperty =
        DependencyProperty.RegisterAttached("AutoSeparators", typeof(bool), typeof(MenuTidy),
            new PropertyMetadata(false, OnChanged));

    public static void SetAutoSeparators(DependencyObject o, bool v) => o.SetValue(AutoSeparatorsProperty, v);
    public static bool GetAutoSeparators(DependencyObject o) => (bool)o.GetValue(AutoSeparatorsProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContextMenu cm) return;
        cm.Opened -= OnOpened;
        if (e.NewValue is true) cm.Opened += OnOpened;
    }

    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ItemsControl menu) return;
        Separator? lastKept = null;
        var contentBefore = false;   // a visible non-separator item seen since the last kept separator
        foreach (var obj in menu.Items)
        {
            switch (obj)
            {
                case Separator sep:
                    if (!contentBefore) { sep.Visibility = Visibility.Collapsed; }       // leading / consecutive
                    else { sep.Visibility = Visibility.Visible; lastKept = sep; contentBefore = false; }
                    break;
                case UIElement el when el.Visibility == Visibility.Visible:
                    contentBefore = true;
                    break;
            }
        }
        if (lastKept != null && !contentBefore) lastKept.Visibility = Visibility.Collapsed;   // trailing
    }
}
