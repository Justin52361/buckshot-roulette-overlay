using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BuckshotOverlay
{
    public static class InjectorEngine
    {
        [Flags]
        public enum ProcessAccessFlags : uint
        {
            All = 0x001F0FFF,
            Terminate = 0x00000001,
            CreateThread = 0x00000002,
            VirtualMemoryOperation = 0x00000008,
            VirtualMemoryRead = 0x00000010,
            VirtualMemoryWrite = 0x00000020,
            DuplicateHandle = 0x00000040,
            CreateProcess = 0x00000080,
            SetQuota = 0x00000100,
            SetInformation = 0x00000200,
            QueryInformation = 0x00000400,
            QueryLimitedInformation = 0x00001000,
            Synchronize = 0x00100000
        }

        [Flags]
        public enum AllocationType
        {
            Commit = 0x1000,
            Reserve = 0x2000,
            Decommit = 0x4000,
            Release = 0x8000,
            Reset = 0x80000,
            Physical = 0x400000,
            TopDown = 0x100000,
            WriteWatch = 0x200000,
            LargePages = 0x20000000
        }

        [Flags]
        public enum MemoryProtection
        {
            Execute = 0x10,
            ExecuteRead = 0x20,
            ExecuteReadWrite = 0x40,
            ExecuteWriteCopy = 0x80,
            NoAccess = 0x01,
            ReadOnly = 0x02,
            ReadWrite = 0x04,
            WriteCopy = 0x08,
            GuardModifierflag = 0x100,
            NoCacheModifierflag = 0x200,
            WriteCombineModifierflag = 0x400
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, AllocationType flAllocationType, MemoryProtection flProtect);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, AllocationType dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, int BufferLengthInBytes, IntPtr PreviousState, IntPtr ReturnLength);

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const int SE_PRIVILEGE_ENABLED = 0x00000002;
        private const string SE_DEBUG_NAME = "SeDebugPrivilege";

        public static void EnableDebugPrivilege()
        {
            try
            {
                IntPtr hToken;
                if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
                {
                    long luid;
                    if (LookupPrivilegeValue(null, SE_DEBUG_NAME, out luid))
                    {
                        TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                        tp.PrivilegeCount = 1;
                        tp.Luid = luid;
                        tp.Attributes = SE_PRIVILEGE_ENABLED;
                        AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                    CloseHandle(hToken);
                }
            }
            catch
            {
                // Silently continue if elevation is not available
            }
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static IntPtr FindProcessMainWindow(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == (uint)processId && IsWindowVisible(hWnd))
                {
                    if (GetWindowRect(hWnd, out RECT rect))
                    {
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;
                        if (width > 200 && height > 200)
                        {
                            found = hWnd;
                            return false;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static bool IsProcessAlive(Process? p)
        {
            if (p == null) return false;
            try
            {
                return !p.HasExited;
            }
            catch
            {
                try
                {
                    return Process.GetProcessById(p.Id) != null;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static Process? FindBuckshotProcess()
        {
            // Match Buckshot Roulette process names
            string[] targetNames = new string[] { "Buckshot Roulette", "Buckshot_Roulette", "buckshot_roulette", "buckshot roulette" };

            Process[] processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    foreach (var target in targetNames)
                    {
                        if (p.ProcessName.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return p;
                        }
                    }
                }
                catch
                {
                    // Ignore inaccessible processes
                }
            }
            return null;
        }

        public static InjectionResult InjectDll(Process targetProcess, string dllPath, Action<string>? log = null)
        {
            log ??= _ => { };

            if (!File.Exists(dllPath))
            {
                log($"[ERROR] Target DLL file not found: {dllPath}");
                return new InjectionResult(false, "DLL file not found on disk.");
            }

            string fullDllPath = Path.GetFullPath(dllPath);
            log($"[INIT] Target Process: {targetProcess.ProcessName} (PID: {targetProcess.Id})");
            log($"[INIT] DLL to Inject: {fullDllPath}");

            EnableDebugPrivilege();

            // 1. Open Target Process
            ProcessAccessFlags flags = ProcessAccessFlags.CreateThread |
                                       ProcessAccessFlags.QueryInformation |
                                       ProcessAccessFlags.VirtualMemoryOperation |
                                       ProcessAccessFlags.VirtualMemoryWrite |
                                       ProcessAccessFlags.VirtualMemoryRead;

            IntPtr hProcess = OpenProcess(flags, false, targetProcess.Id);
            if (hProcess == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                log($"[ERROR] OpenProcess failed with error code: {error} (Requires Administrator rights if game runs elevated)");
                return new InjectionResult(false, $"OpenProcess failed with error {error}. Try running as Administrator.");
            }
            log($"[SUCCESS] Process handle acquired: 0x{hProcess.ToInt64():X}");

            try
            {
                // 2. Allocate memory in target process for DLL path
                byte[] pathBytes = Encoding.Unicode.GetBytes(fullDllPath + "\0");
                uint allocSize = (uint)pathBytes.Length;

                IntPtr allocAddr = VirtualAllocEx(hProcess, IntPtr.Zero, allocSize, AllocationType.Commit | AllocationType.Reserve, MemoryProtection.ReadWrite);
                if (allocAddr == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    log($"[ERROR] VirtualAllocEx failed with error code: {error}");
                    return new InjectionResult(false, $"VirtualAllocEx failed with error {error}.");
                }
                log($"[SUCCESS] Remote memory allocated at: 0x{allocAddr.ToInt64():X} ({allocSize} bytes)");

                try
                {
                    // 3. Write DLL path into target process
                    if (!WriteProcessMemory(hProcess, allocAddr, pathBytes, allocSize, out UIntPtr bytesWritten))
                    {
                        int error = Marshal.GetLastWin32Error();
                        log($"[ERROR] WriteProcessMemory failed with error code: {error}");
                        return new InjectionResult(false, $"WriteProcessMemory failed with error {error}.");
                    }
                    log($"[SUCCESS] DLL path written into target process ({bytesWritten} bytes)");

                    // 4. Find LoadLibraryW address in kernel32.dll
                    IntPtr hKernel32 = GetModuleHandle("kernel32.dll");
                    if (hKernel32 == IntPtr.Zero)
                    {
                        log("[ERROR] GetModuleHandle('kernel32.dll') returned NULL");
                        return new InjectionResult(false, "Could not resolve kernel32.dll");
                    }

                    IntPtr pLoadLibraryW = GetProcAddress(hKernel32, "LoadLibraryW");
                    if (pLoadLibraryW == IntPtr.Zero)
                    {
                        log("[ERROR] GetProcAddress('LoadLibraryW') returned NULL");
                        return new InjectionResult(false, "Could not resolve LoadLibraryW address");
                    }
                    log($"[SUCCESS] LoadLibraryW address: 0x{pLoadLibraryW.ToInt64():X}");

                    // 5. Create Remote Thread
                    IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, pLoadLibraryW, allocAddr, 0, out uint threadId);
                    if (hThread == IntPtr.Zero)
                    {
                        int error = Marshal.GetLastWin32Error();
                        log($"[ERROR] CreateRemoteThread failed with error code: {error}");
                        return new InjectionResult(false, $"CreateRemoteThread failed with error {error}.");
                    }
                    log($"[SUCCESS] Remote thread created! Thread ID: {threadId}");

                    // 6. Wait for thread to finish
                    uint waitResult = WaitForSingleObject(hThread, 5000);
                    if (waitResult == 0) // WAIT_OBJECT_0
                    {
                        GetExitCodeThread(hThread, out uint exitCode);
                        CloseHandle(hThread);

                        if (exitCode == 0)
                        {
                            log($"[WARNING] LoadLibraryW returned 0 (ExitCode 0). DLL might have failed to initialize.");
                            return new InjectionResult(false, "Remote LoadLibraryW returned 0.");
                        }

                        log($"[SUCCESS] Injection Complete! Remote module handle: 0x{exitCode:X}");
                        return new InjectionResult(true, $"Successfully injected DLL into PID {targetProcess.Id} (Module: 0x{exitCode:X})");
                    }
                    else
                    {
                        CloseHandle(hThread);
                        log($"[WARNING] Remote thread wait timed out or failed (Code: {waitResult})");
                        return new InjectionResult(true, "Remote thread launched (timeout reached).");
                    }
                }
                finally
                {
                    // Clean up allocated path memory
                    VirtualFreeEx(hProcess, allocAddr, 0, AllocationType.Release);
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
    }

    public record InjectionResult(bool Success, string Message);
}
