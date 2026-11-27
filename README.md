# Eurocat Flight Plan Window (vatSys plugin)

Shows its own Flight Plan Window in place of the vatSys Flight Plan Window. It has the layout of the Eurocat (TAAATS)
window, and it does the same vatSys actions as the vatSys window.

The plugin is a WPF window built on [vatSysToolbox](https://github.com/YuKitsune/vatSysToolbox).

## Build

```powershell
git submodule update --init
.\build.ps1
```

This needs the .NET SDK and a vatSys installation. The script builds the plugin, merges its dependencies into one
assembly with ILRepack, and writes:

- `artifacts\FlightPlanWindowPlugin\` - `FlightPlanWindowPlugin.dll` and `0Harmony.dll`
- `artifacts\FlightPlanWindowPlugin.<version>.zip` - the same two files

If vatSys is not at the path in the registry, pass `-VatSysPath "<path>"` (the directory that has `bin`). Pass
`-Version 1.2.3` to set the version.

To build without the merge (for example, in an IDE), build `source\FlightPlanWindowPlugin\FlightPlanWindowPlugin.csproj`.

## Install

Copy `FlightPlanWindowPlugin.dll` and `0Harmony.dll` to a directory in the `Plugins` directory of the profile (for
example, `Documents\vatSys Files\Profiles\Australia\Plugins\FlightPlanWindowPlugin\`), then start vatSys again.
Remove the directory to remove the plugin.

## Use

| Button | Action |
| --- | --- |
| CRE | Create a flight plan for the callsign in ACID. Press Enter to apply. |
| MOD | Modify the flight plan. Press Enter to apply. |
| EST | Activate the flight plan. This opens the vatSys estimate window. |
| BACK | Deactivate a coordinated flight plan. |
| DEP | Set or clear the ATD. An empty ATD sets the time now, or cancels the departure. |
| CNL | Cancel the flight plan. |
| ACC / HND | Accept jurisdiction / open the handoff menu. |
| INHI | Inhibit, or stop the inhibit. |
| PRL / CFL | Open the vatSys PRL and CFL menus. |
| NCOU | Decouple the radar track, and stop the automatic couple. |
| BCOU | Couple to the selected radar track, or start the automatic couple again. |
| RTE | Show or hide the graphic route on the ASD. |
| Clear / Escape | Clear the window / stop the current change. |

- Type in ASSR and OPDATA, and press Enter to apply them.
- REGN shows and changes the `REG/` item in OTHER.
- PSSR shows the SSR code that the coupled aircraft transmits.
- The title bar has the same buttons as the other vatSys windows: close, minimise, and send to back.

vatSys has no function for the other buttons and fields, thus they are not available. The runway and the SID or STAR
are not in this window. Set them from the label or the strip.

If the window cannot open (for example, after a vatSys update), the vatSys Flight Plan Window opens.

### Use the vatSys Flight Plan Window

To use the vatSys Flight Plan Window, remove the check mark from **Settings**, **Eurocat Flight Plan Window** in the
vatSys menu bar. Select the item again to use the window of the plugin. A window that is open does not change. The
selection applies to the next window that opens.

The plugin keeps the selection in `Documents\vatSys Files\EurocatFlightPlanWindow\Settings.json`, thus vatSys starts
with the same window the next time. The selection is the same for all profiles.

## Messages

Other plugins can select the window with the toolbox messenger. The mailbox of this plugin is
`dev.eoinmotherway.flight-plan-window` (see `FlightPlanWindowMessages.cs`).

| Message | Direction | Meaning |
| --- | --- | --- |
| `dev.eoinmotherway.flight-plan-window.toggle` | To this plugin | Selects the other Flight Plan Window. |
| `dev.eoinmotherway.flight-plan-window.enabled` | From this plugin | The window of this plugin is selected. |
| `dev.eoinmotherway.flight-plan-window.disabled` | From this plugin | The vatSys window is selected. |

The plugin sends the selection to all plugins when it changes. When the plugin starts, it sends the selection to the
Eurocat Menus plugin (`dev.eoinmotherway.eurocatmenus`).

## How it works

vatSys has no extension point for the Flight Plan Window, thus the plugin patches `MMI.OpenFPWindow` with Harmony
(`FlightPlanWindowPatches.cs`). A second patch on the `Load` handler of the vatSys window finds a vatSys window that
the first patch did not stop, and replaces it before it shows.

The plugin gets the vatSys members that are not public by reflection (`FlightPlanInternals.cs`).

## Repository

- `source\FlightPlanWindowPlugin\` - the plugin.
- `external\vatSysToolbox\` - the toolbox, as a git submodule.
- `docs\` - screenshots of the first version of the plugin, which was a WinForms window. That version is the first
  commit of this repository.
