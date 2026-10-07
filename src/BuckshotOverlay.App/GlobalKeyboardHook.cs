using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BuckshotOverlay
{
    public class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelKeyboardProc? _staticProc;
        private IntPtr _hookId = IntPtr.Zero;

        public event Action<Keys>? KeyPressed;

        public GlobalKeyboardHook()
        {
            _staticProc = HookCallback;
            _hookId = SetHook(_staticProc);
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            try
            {
                IntPtr hMod = GetModuleHandle("user32.dll");
                if (hMod == IntPtr.Zero)
                {
                    using (Process curProcess = Process.GetCurrentProcess())
                    using (ProcessModule? curModule = curProcess.MainModule)
                    {
                        string? moduleName = curModule?.ModuleName;
                        if (moduleName != null) hMod = GetModuleHandle(moduleName);
                    }
                }
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, hMod, 0);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
                {
                    int vkCode = Marshal.ReadInt32(lParam);
                    Keys key = (Keys)vkCode;
                    KeyPressed?.Invoke(key);
                }
            }
            catch
            {
                // Silently ignore callback exceptions
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
