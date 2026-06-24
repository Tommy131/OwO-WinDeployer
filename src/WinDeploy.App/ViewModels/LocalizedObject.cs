using System.Windows;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels;

/// <summary>
/// Base for ViewModels that expose localized text and must refresh when the language changes.
/// Subscribes to <see cref="Localizer.CultureChanged"/> and marshals the callback to the UI thread,
/// then calls <see cref="OnCultureChanged"/> where the VM raises <c>OnPropertyChanged</c> for each
/// localized property.
///
/// Long-lived VMs (nav items, page VMs, install cards) can rely on the static subscription for the
/// app lifetime. Transient VMs (per-click detail VMs, dialogs) MUST be <see cref="Dispose"/>d to
/// unsubscribe, otherwise the static event keeps them alive.
/// </summary>
public abstract class LocalizedObject : ObservableObject, IDisposable
{
    private bool _disposed;

    protected LocalizedObject()
    {
        Localizer.CultureChanged += OnCultureChangedInternal;
    }

    private void OnCultureChangedInternal()
    {
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) d.Invoke(OnCultureChanged);
        else OnCultureChanged();
    }

    /// <summary>Called on the UI thread when the language changes. Default raises a blanket
    /// <c>PropertyChanged(string.Empty)</c> so WPF re-reads every binding on this object — which refreshes
    /// any <c>=> Localizer.T(…)</c> computed property for free. Override to ALSO reload cached/async data
    /// (call <c>base.OnCultureChanged()</c> to keep the blanket refresh).</summary>
    protected virtual void OnCultureChanged() => OnPropertyChanged(string.Empty);

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Localizer.CultureChanged -= OnCultureChangedInternal;
    }
}
