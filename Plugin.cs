using System;
using System.ComponentModel.Composition;
using vatsys;
using vatsys.Plugin;

namespace EurocatFPWindow
{
    /// <summary>
    /// vatSys plugin entry point. Replaces the stock Flight Plan Window with a
    /// Eurocat-layout one by patching <c>MMI.OpenFPWindow</c> at load time.
    /// </summary>
    [Export(typeof(IPlugin))]
    public class Plugin : IPlugin
    {
        public const string PluginName = "Eurocat FP Window";

        public string Name => PluginName;

        public Plugin()
        {
            try
            {
                Patches.Apply();
            }
            catch (Exception ex)
            {
                // Never take vatSys down: if patching fails the stock window keeps working.
                Report(new Exception("Could not replace the Flight Plan Window; using the stock window. " + ex.Message, ex));
            }
        }

        public void OnFDRUpdate(FDP2.FDR updated)
        {
        }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated)
        {
        }

        internal static void Report(Exception ex)
        {
            try
            {
                Errors.Add(ex, PluginName);
            }
            catch
            {
                // Errors list unavailable (e.g. running outside vatSys) - nothing else to do.
            }
        }
    }
}
