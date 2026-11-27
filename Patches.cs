using System;
using System.Reflection;
using System.Windows.Forms;
using HarmonyLib;
using vatsys;

namespace EurocatFPWindow
{
    /// <summary>
    /// Harmony patches that redirect every "open flight plan window" request to
    /// <see cref="EurocatFlightPlanWindow"/>.
    ///
    /// Two layers:
    ///  1. A prefix on both <c>MMI.OpenFPWindow</c> overloads - the normal path.
    ///  2. A prefix on the stock window's Load handler. <c>OpenFPWindow</c> is small
    ///     enough that the JIT may inline it into a caller, in which case layer 1 is
    ///     skipped for that caller. The Load handler is invoked through a delegate and
    ///     can never be inlined, so it catches any stock window that slips through and
    ///     swaps it before it becomes visible.
    /// </summary>
    internal static class Patches
    {
        private const string HarmonyId = "eurocatfpwindow.plugin";

        private static Harmony harmony;
        private static FieldInfo stockFdrField;

        /// <summary>Set while we deliberately let a stock window open (our own window failed).</summary>
        [ThreadStatic]
        private static bool allowStock;

        /// <summary>True when the last stock window was caught by the Load fallback (layer 2).</summary>
        internal static bool LastOpenUsedFallback { get; private set; }

        public static void Apply()
        {
            if (harmony != null)
            {
                return;
            }

            var h = new Harmony(HarmonyId);

            MethodInfo openWithFdr = AccessTools.Method(typeof(MMI), "OpenFPWindow", new[] { typeof(FDP2.FDR) });
            MethodInfo openEmpty = AccessTools.Method(typeof(MMI), "OpenFPWindow", Type.EmptyTypes);
            if (openWithFdr == null)
            {
                throw new MissingMethodException("vatsys.MMI", "OpenFPWindow(FDR)");
            }

            h.Patch(openWithFdr, prefix: new HarmonyMethod(typeof(Patches), nameof(OpenWithFdrPrefix)));
            if (openEmpty != null)
            {
                h.Patch(openEmpty, prefix: new HarmonyMethod(typeof(Patches), nameof(OpenEmptyPrefix)));
            }

            Type stock = typeof(MMI).Assembly.GetType("vatsys.FlightPlanWindow");
            MethodInfo stockLoad = stock == null ? null : AccessTools.Method(stock, "FlightPlanWindow_Load");
            if (stockLoad != null)
            {
                stockFdrField = AccessTools.Field(stock, "_fdr");
                h.Patch(stockLoad, prefix: new HarmonyMethod(typeof(Patches), nameof(StockLoadPrefix)));
            }

            harmony = h;
        }

        public static void Remove()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }

        private static bool OpenWithFdrPrefix(FDP2.FDR fdr)
        {
            return !TryOpen(fdr);
        }

        private static bool OpenEmptyPrefix()
        {
            return !TryOpen(null);
        }

        /// <summary>Opens our window. Returns false (so the stock window opens instead) on failure.</summary>
        private static bool TryOpen(FDP2.FDR fdr)
        {
            if (allowStock)
            {
                return false;
            }

            try
            {
                LastOpenUsedFallback = false;
                EurocatFlightPlanWindow.Open(fdr);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Report(new Exception("Eurocat Flight Plan Window failed to open; using the stock window. " + ex.Message, ex));
                allowStock = true;
                return false;
            }
        }

        private static void StockLoadPrefix(object __instance)
        {
            if (allowStock)
            {
                // We chose to fall back to this stock window - leave it alone.
                allowStock = false;
                return;
            }

            if (!(__instance is Form stockForm))
            {
                return;
            }

            try
            {
                var fdr = stockFdrField?.GetValue(stockForm) as FDP2.FDR;
                EurocatFlightPlanWindow.Open(fdr);
                LastOpenUsedFallback = true;

                // Keep the stock window invisible and close it once its Show() call has returned.
                stockForm.Opacity = 0;
                stockForm.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
                {
                    try
                    {
                        stockForm.Close();
                        stockForm.Dispose();
                    }
                    catch
                    {
                    }
                }));
            }
            catch (Exception ex)
            {
                Plugin.Report(new Exception("Eurocat Flight Plan Window failed to open; using the stock window. " + ex.Message, ex));
            }
        }
    }
}
