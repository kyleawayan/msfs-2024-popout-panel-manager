using MSFSPopoutPanelManager.DomainModel.Profile;
using MSFSPopoutPanelManager.DomainModel.Setting;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MSFSPopoutPanelManager.WindowsAgent
{
    // Refocuses MSFS (and brings the cursor home) when the user interacts with a designated
    // "refocus on display" monitor - typically a touchscreen running instrument-panel apps.
    //
    // Detecting the touch itself is unreliable: Chromium-based apps swallow touch through the
    // modern pointer pipeline (invisible to a WH_MOUSE_LL hook), and Air Manager panels are
    // no-activate windows that never become the foreground window (invisible to a foreground
    // watcher - and MSFS keeps focus the whole time, so there is nothing to "refocus").
    //
    // The one signal common to every case is the cursor: touching any window moves the system
    // cursor to the touch point. So this polls the cursor position - when it sits on a
    // RefocusDisplay monitor and the user goes idle, MSFS is brought back to the foreground and
    // the cursor is moved to its centre. No synthetic input, so touch gestures (swipe, pinch-zoom)
    // are unaffected.
    public class RefocusOnDisplayManager
    {
        private static CancellationTokenSource _cts;
        private static bool _hasRefocused;

        private const int PollIntervalMs = 250;

        public static UserProfile ActiveProfile { private get; set; }

        public static ApplicationSetting ApplicationSetting { private get; set; }

        public static bool IsHooked => _cts != null;

        public static void Hook()
        {
            if (ActiveProfile?.PanelConfigs == null)
                return;

            UnHook();

            if (!ActiveProfile.PanelConfigs.Any(p => p.PanelType == PanelType.RefocusDisplay))
                return;

            Debug.WriteLine("Executing RefocusOnDisplayManager cursor poll start...");

            _hasRefocused = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => PollLoop(token), token);
        }

        public static void UnHook()
        {
            if (_cts == null)
                return;

            Debug.WriteLine("Executing RefocusOnDisplayManager cursor poll stop...");
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        private static void PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (IsCursorOnRefocusDisplay())
                    {
                        var delayMs = Convert.ToInt32(ApplicationSetting.RefocusSetting.RefocusGameWindow.Delay * 1000);

                        // Wait for the user to stop interacting (system-wide idle), then refocus once.
                        if (!_hasRefocused && GetIdleTimeMs() >= delayMs)
                        {
                            RefocusMsfs();
                            _hasRefocused = true;
                        }
                    }
                    else
                    {
                        // Cursor left the monitor - arm again for the next interaction.
                        _hasRefocused = false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"RefocusOnDisplayManager poll error: {ex.Message}");
                }

                Thread.Sleep(PollIntervalMs);
            }
        }

        private static bool IsCursorOnRefocusDisplay()
        {
            if (!PInvoke.GetCursorPos(out var point))
                return false;

            return ActiveProfile.PanelConfigs
                .Where(p => p.PanelType == PanelType.RefocusDisplay)
                .Any(p => point.X >= p.Left && point.X < p.Left + p.Width
                          && point.Y >= p.Top && point.Y < p.Top + p.Height);
        }

        private static void RefocusMsfs()
        {
            var handle = WindowProcessManager.SimulatorProcess?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
                return;

            Debug.WriteLine($"{DateTime.Now} - RefocusOnDisplayManager refocusing MSFS");

            // For focus-stealing apps (Chrome etc.) bring MSFS back. For no-activate apps
            // (Air Manager) MSFS never lost focus - this is a no-op and only the cursor moves.
            // AttachThreadInput-based steal - proven to work cross-process. No synthetic click.
            if (!WindowActionManager.IsMsfsInFocus())
                WindowActionManager.SetWindowFocus(handle);

            var rect = WindowActionManager.GetWindowRectangle(handle);
            PInvoke.SetCursorPos(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        }

        private static int GetIdleTimeMs()
        {
            var lastInputInfo = new LASTINPUTINFO();
            lastInputInfo.cbSize = (uint)Marshal.SizeOf(lastInputInfo);

            if (!PInvoke.GetLastInputInfo(ref lastInputInfo))
                return int.MaxValue;

            return Environment.TickCount - (int)lastInputInfo.dwTime;
        }
    }
}
