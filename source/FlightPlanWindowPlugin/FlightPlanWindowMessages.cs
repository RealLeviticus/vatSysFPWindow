namespace FlightPlanWindowPlugin;

/// <summary>
/// The messages that other plugins (for example, the buttons of the Eurocat Menus plugin) use to select the Flight
/// Plan Window. Send these messages to the mailbox <see cref="Plugin.MailboxName"/>.
/// </summary>
public static class FlightPlanWindowMessages
{
    /// <summary>
    /// Selects the other Flight Plan Window: the window of this plugin, or the vatSys window.
    /// </summary>
    public const string Toggle = "dev.eoinmotherway.flight-plan-window.toggle";

    /// <summary>
    /// The plugin sends this message when the Flight Plan Window of this plugin is selected.
    /// </summary>
    public const string Enabled = "dev.eoinmotherway.flight-plan-window.enabled";

    /// <summary>
    /// The plugin sends this message when the vatSys Flight Plan Window is selected.
    /// </summary>
    public const string Disabled = "dev.eoinmotherway.flight-plan-window.disabled";
}
