using vatSysToolbox;
using vatSysToolbox.Messaging;
using vatSysToolbox.Windows;

namespace FlightPlanWindowPlugin;

/// <summary>
/// Selects the Flight Plan Window that opens: the window of this plugin, or the vatSys window. The selection stays
/// for the next time that vatSys starts.
/// </summary>
/// <remarks>
/// <para>
/// A window that is open does not change. The selection applies to the next window that opens.
/// </para>
/// <para>
/// Make this class before the messenger starts, because it subscribes to its message. Use it on the UI thread.
/// </para>
/// </remarks>
public class FlightPlanWindowSwitch
{
    // The Eurocat Menus plugin shows the selection on a toggle button
    const string EurocatMenusMailboxName = "dev.eoinmotherway.eurocatmenus";

    readonly FlightPlanWindowSettings _settings;
    readonly IPluginMessenger _messenger;
    readonly IErrorReporter _errorReporter;

    public FlightPlanWindowSwitch(
        FlightPlanWindowSettings settings,
        IPluginMessenger messenger,
        IGuiInvoker guiInvoker,
        IErrorReporter errorReporter)
    {
        _settings = settings;
        _messenger = messenger;
        _errorReporter = errorReporter;

        // The messenger receives the messages on a background thread
        messenger.Subscribe(FlightPlanWindowMessages.Toggle, (_, _) =>
        {
            guiInvoker.InvokeOnGUI(_ => Toggle());
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// <c>true</c> if the window of this plugin opens, and <c>false</c> if the vatSys window opens.
    /// </summary>
    public bool IsEnabled => FlightPlanWindowPatches.IsEnabled;

    public event EventHandler? IsEnabledChanged;

    string StateMessage => IsEnabled ? FlightPlanWindowMessages.Enabled : FlightPlanWindowMessages.Disabled;

    /// <summary>
    /// Selects the other window.
    /// </summary>
    public void Toggle()
    {
        var isEnabled = !FlightPlanWindowPatches.IsEnabled;

        FlightPlanWindowPatches.IsEnabled = isEnabled;
        _settings.Save(isEnabled);

        IsEnabledChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            _messenger.Broadcast(StateMessage);
        }
        catch (Exception ex)
        {
            _errorReporter.ReportError(ex, "Cannot send the selection of the Flight Plan Window");
        }
    }

    /// <summary>
    /// Sends the selection to the Eurocat Menus plugin when the plugin starts, so that the menu shows it. Call this
    /// method after the messenger starts.
    /// </summary>
    public void SendCurrentState()
    {
        try
        {
            _messenger.Send(EurocatMenusMailboxName, StateMessage);
        }
        catch (Exception ex)
        {
            _errorReporter.ReportError(ex, "Cannot send the selection of the Flight Plan Window");
        }
    }
}
