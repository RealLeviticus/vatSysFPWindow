using System.ComponentModel.Composition;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using vatsys;
using vatsys.Plugin;
using vatSysToolbox;
using vatSysToolbox.Messaging;
using vatSysToolbox.Windows;

namespace FlightPlanWindowPlugin;

/// <summary>
/// Shows its own Flight Plan Window in place of the vatSys Flight Plan Window. Other plugins can select the window
/// with <see cref="FlightPlanWindowMessages"/>.
/// </summary>
[Export(typeof(IPlugin))]
public class Plugin : IPlugin
{
    public const string Name = "Eurocat Flight Plan Window";

    /// <summary>
    /// The name that other plugins use to send messages to this plugin.
    /// </summary>
    public const string MailboxName = "dev.eoinmotherway.flight-plan-window";

    // The text of the item in the vatSys Settings menu that selects the Flight Plan Window of this plugin
    const string SettingsItemText = "Eurocat Flight Plan Window";

    readonly IErrorReporter _errorReporter;
    readonly FlightPlanWindow _flightPlanWindow;

    string IPlugin.Name => Name;

    public Plugin()
    {
        DpiAwareness.EnsureDpiAwareness();

        var serviceProvider = new ServiceCollection()
            .AddLogging()
            .AddErrorReporter(Name)
            .AddWindowManager()
            .AddPluginMessenger(MailboxName)
            .BuildServiceProvider();

        _errorReporter = serviceProvider.GetRequiredService<IErrorReporter>();
        var messenger = serviceProvider.GetRequiredService<IPluginMessenger>();

        // Read the selection before the patches apply, so that the first window that opens is the selected window
        var settings = new FlightPlanWindowSettings(FlightPlanWindowSettings.DefaultFilePath, _errorReporter);
        FlightPlanWindowPatches.IsEnabled = settings.Load();

        _flightPlanWindow = new FlightPlanWindow(
            serviceProvider.GetRequiredService<WindowManager>(),
            _errorReporter);

        try
        {
            FlightPlanWindowPatches.Apply(_errorReporter, _flightPlanWindow.Open);
        }
        catch (Exception ex)
        {
            _errorReporter.ReportError(ex, "Cannot replace the vatSys Flight Plan Window. The vatSys window shows.");
        }

        // The switch subscribes to its message. Thus, start the messenger after the switch is made.
        var flightPlanWindowSwitch = new FlightPlanWindowSwitch(
            settings,
            messenger,
            serviceProvider.GetRequiredService<IGuiInvoker>(),
            _errorReporter);

        messenger.Start();
        flightPlanWindowSwitch.SendCurrentState();

        AddSettingsItem(flightPlanWindowSwitch);

        // vatSys loads the plugins while it creates the main form. The main form is ready only after the message
        // loop starts, thus load the window when the message loop is idle for the first time.
        Application.Idle += OnFirstIdle;
    }

    void OnFirstIdle(object sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;

        // Load the Flight Plan Window once now, so that the first window opens as fast as the next windows.
        // An exception that is not caught on the UI thread stops vatSys.
        try
        {
            _flightPlanWindow.WarmUp();
        }
        catch (Exception ex)
        {
            _errorReporter.ReportError(ex, "Cannot load the Flight Plan Window");
        }
    }

    // Selects the Flight Plan Window of this plugin or the vatSys window, from the vatSys Settings menu. The check mark
    // shows if the window of this plugin is selected.
    void AddSettingsItem(FlightPlanWindowSwitch flightPlanWindowSwitch)
    {
        var item = new ToolStripMenuItem(SettingsItemText)
        {
            Checked = flightPlanWindowSwitch.IsEnabled
        };
        flightPlanWindowSwitch.IsEnabledChanged += (_, _) => item.Checked = flightPlanWindowSwitch.IsEnabled;
        item.Click += (_, _) =>
        {
            // An exception that is not caught on the UI thread stops vatSys.
            try
            {
                flightPlanWindowSwitch.Toggle();
            }
            catch (Exception ex)
            {
                _errorReporter.ReportError(ex, "Cannot select the Flight Plan Window");
            }
        };

        MMI.AddCustomMenuItem(new CustomToolStripMenuItem(
            CustomToolStripMenuItemWindowType.Main,
            CustomToolStripMenuItemCategory.Settings,
            item));
    }

    public void OnFDRUpdate(FDP2.FDR updated) { }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated) { }
}
