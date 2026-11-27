using System;
using System.Reflection;
using System.Windows.Forms;
using HarmonyLib;
using vatsys;

namespace EurocatFPWindow
{
    /// <summary>
    /// Reflection access to the handful of vatSys members the stock Flight Plan Window
    /// uses that are not public. Everything here degrades gracefully: if a vatSys update
    /// renames something, the affected feature reports an error instead of crashing.
    /// </summary>
    internal static class Internals
    {
        private static readonly FieldInfo MainFormField = AccessTools.Field(typeof(MMI), "MainForm");
        private static readonly FieldInfo NetworkInstanceField = AccessTools.Field(typeof(Network), "Instance");
        private static readonly MethodInfo SendFlightPlanChangeMethod = AccessTools.Method(typeof(Network), "SendFlightPlanChange", new[] { typeof(FDP2.FDR) });
        private static readonly MethodInfo SendNewFlightPlanMethod = AccessTools.Method(typeof(Network), "SendNewFlightPlan", new[] { typeof(FDP2.FDR) });
        private static readonly MethodInfo SendFPRequestMethod = AccessTools.Method(typeof(Network), "SendFPRequest", new[] { typeof(string) });
        private static readonly MethodInfo SendCancelESTMethod = AccessTools.Method(typeof(Network), "SendCancelEST",
            new[] { typeof(FDP2.FDR), typeof(FDP2.FDR.ExtractedRoute.Segment), typeof(ClientQueryEstimateType) });
        private static readonly EventInfo NoFPExistsEvent = typeof(Network).GetEvent("NoFPExists", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        // Looked up in vatSys' own assembly. (AccessTools.TypeByName would enumerate every type of
        // every loaded assembly, which takes noticeable time with a full set of plugins loaded.)
        private static readonly Type ESTWindowType = typeof(MMI).Assembly.GetType("vatsys.ESTWindow");
        private static readonly Type ConfirmationWindowType = typeof(MMI).Assembly.GetType("vatsys.GenericConfirmationWindow");

        public static Form MainForm => MainFormField?.GetValue(null) as Form;

        private static object NetworkInstance
        {
            get
            {
                object instance = NetworkInstanceField?.GetValue(null);
                if (instance == null)
                {
                    throw new InvalidOperationException("vatSys network is not available");
                }
                return instance;
            }
        }

        private static void InvokeNetwork(MethodInfo method, string name, params object[] args)
        {
            if (method == null)
            {
                throw new MissingMethodException("vatsys.Network", name);
            }
            try
            {
                method.Invoke(NetworkInstance, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        public static void SendFlightPlanChange(FDP2.FDR fdr) => InvokeNetwork(SendFlightPlanChangeMethod, "SendFlightPlanChange", fdr);

        public static void SendNewFlightPlan(FDP2.FDR fdr) => InvokeNetwork(SendNewFlightPlanMethod, "SendNewFlightPlan", fdr);

        public static void SendFPRequest(string callsign) => InvokeNetwork(SendFPRequestMethod, "SendFPRequest", callsign);

        public static void SendCancelEST(FDP2.FDR fdr, FDP2.FDR.ExtractedRoute.Segment position) =>
            InvokeNetwork(SendCancelESTMethod, "SendCancelEST", fdr, position, ClientQueryEstimateType.ETO);

        /// <summary>
        /// Subscribes to the internal <c>Network.NoFPExists</c> event. The callback receives the callsign.
        /// Returns an action that unsubscribes, or null if the event could not be hooked.
        /// </summary>
        public static Action SubscribeNoFPExists(Action<string> callback)
        {
            if (NoFPExistsEvent == null)
            {
                return null;
            }

            try
            {
                var relay = new NoFPRelay(callback);
                MethodInfo relayMethod = typeof(NoFPRelay).GetMethod(nameof(NoFPRelay.Handle));
                // EventHandler<NoFPExistsEventArgs> accepts a method taking EventArgs (contravariance).
                Delegate handler = Delegate.CreateDelegate(NoFPExistsEvent.EventHandlerType, relay, relayMethod);
                MethodInfo add = NoFPExistsEvent.GetAddMethod(true);
                MethodInfo remove = NoFPExistsEvent.GetRemoveMethod(true);
                add.Invoke(null, new object[] { handler });
                return () =>
                {
                    try
                    {
                        remove.Invoke(null, new object[] { handler });
                    }
                    catch
                    {
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private sealed class NoFPRelay
        {
            private readonly Action<string> callback;

            public NoFPRelay(Action<string> callback)
            {
                this.callback = callback;
            }

            public void Handle(object sender, EventArgs e)
            {
                string callsign = null;
                try
                {
                    callsign = Traverse.Create(e).Property("Callsign").GetValue<string>()
                               ?? Traverse.Create(e).Field("Callsign").GetValue<string>();
                }
                catch
                {
                }
                callback(callsign);
            }
        }

        /// <summary>Shows vatSys' own estimate (activation) dialog for the FDR.</summary>
        public static void ShowESTWindow(FDP2.FDR fdr)
        {
            if (ESTWindowType == null)
            {
                throw new TypeLoadException("vatsys.ESTWindow not found");
            }
            using (var window = (Form)Activator.CreateInstance(ESTWindowType,
                       BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { fdr }, null))
            {
                window.ShowDialog();
            }
        }

        /// <summary>Shows vatSys' own Yes/No confirmation window.</summary>
        public static bool Confirm(string title, string message)
        {
            MethodInfo show = ConfirmationWindowType == null
                ? null
                : AccessTools.Method(ConfirmationWindowType, "Show", new[] { typeof(string), typeof(string) });
            if (show == null)
            {
                return MessageBox.Show(message, title, MessageBoxButtons.YesNo) == DialogResult.Yes;
            }
            return (DialogResult)show.Invoke(null, new object[] { title, message }) == DialogResult.Yes;
        }

    }
}
