using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShakeConvert
{
    /// <summary>
    /// Global mouse hook on a DEDICATED thread: Windows calls it on every mouse move
    /// system-wide, so it must never wait for the app's UI (otherwise the cursor lags everywhere).
    /// Detects the "shake" (fast back-and-forth) while the left button is held down.
    /// </summary>
    internal sealed class ShakeDetector : IDisposable
    {
        public event Action<int, int> Shaken;   // raised on the hook thread: listeners must marshal to the UI
        public event Action ButtonUp;

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Native.POINT pt; }

        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        const uint WM_QUIT = 0x0012;

        readonly Native.LowLevelMouseProc _proc; // kept alive against the GC
        IntPtr _hook;
        Thread _thread;
        uint _threadId;

        // state (used only by the hook thread)
        bool _down, _fired;
        int _startX, _peak, _dir;
        readonly Queue<uint> _reversals = new Queue<uint>();

        volatile int _amplitude = 40;   // minimum px to count a direction change
        volatile int _needed = 4;       // direction changes required
        volatile int _windowMs = 900;   // within this time

        public ShakeDetector() { _proc = HookProc; }

        /// <param name="level">0 = low, 1 = medium, 2 = high</param>
        public void SetSensitivity(int level)
        {
            switch (level)
            {
                case 0: _amplitude = 60; _needed = 5; _windowMs = 900; break;
                case 2: _amplitude = 28; _needed = 3; _windowMs = 1000; break;
                default: _amplitude = 40; _needed = 4; _windowMs = 900; break;
            }
        }

        public void Start()
        {
            if (_thread != null) return;
            using var ready = new ManualResetEventSlim(false);
            Exception failure = null;
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) failure = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                ready.Set();
                if (_hook == IntPtr.Zero) return;

                // minimal message loop: only needed to receive the hook calls
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                Native.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            })
            { IsBackground = true, Name = "ShakeConvert mouse hook", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            ready.Wait(3000);
            if (failure != null) throw failure;
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == Native.WM_MOUSEMOVE)
                {
                    if (_down && !_fired)
                    {
                        var d = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                        Track(d.pt.X, d.pt.Y, d.time);
                    }
                }
                else if (msg == Native.WM_LBUTTONDOWN)
                {
                    var d = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    _down = true; _fired = false; _dir = 0;
                    _startX = _peak = d.pt.X;
                    _reversals.Clear();
                }
                else if (msg == Native.WM_LBUTTONUP && _down)
                {
                    _down = false;
                    ButtonUp?.Invoke();
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        void Track(int x, int y, uint t)
        {
            int amp = _amplitude;
            if (_dir == 0)
            {
                if (Math.Abs(x - _startX) >= amp) { _dir = Math.Sign(x - _startX); _peak = x; }
                return;
            }

            bool reversed = false;
            if (_dir > 0)
            {
                if (x > _peak) _peak = x;
                else if (_peak - x >= amp) { reversed = true; _dir = -1; _peak = x; }
            }
            else
            {
                if (x < _peak) _peak = x;
                else if (x - _peak >= amp) { reversed = true; _dir = 1; _peak = x; }
            }
            if (!reversed) return;

            _reversals.Enqueue(t);
            while (_reversals.Count > 0 && t - _reversals.Peek() > (uint)_windowMs) _reversals.Dequeue();

            if (_reversals.Count >= _needed)
            {
                _fired = true; // a single menu per drag
                Shaken?.Invoke(x, y);
            }
        }

        public void Dispose()
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(1000);
            _thread = null;
        }
    }
}
