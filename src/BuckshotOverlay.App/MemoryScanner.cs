using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace BuckshotOverlay
{
    public class MemoryScanner
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION64
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public uint AllocationProtect;
            public uint __alignment1;
            public ulong RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint __alignment2;
        }

        private const uint MEM_COMMIT = 0x1000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll")]
        private static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION64 lpBuffer, uint dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        public record ShellScanResult(int LiveCount, int BlankCount, List<string> Sequence, ulong FoundAddress);
        public record CandidateInfo(ShellScanResult Result, int Stride, int PtrOffset)
        {
            public string Signature => $"{Result.FoundAddress:X}:{string.Join(",", Result.Sequence)}";
        }

        private static int s_cachedPid = 0;
        private static IntPtr s_cachedProcessHandle = IntPtr.Zero;

        private static ulong s_lockedAddress = 0;
        private static int s_lockedStride = 24;
        private static int s_lockedPtrOffset = 8;
        private static int s_expectedLive = -1;
        private static int s_expectedBlank = -1;

        public static void ClearLock()
        {
            s_lockedAddress = 0;
            s_expectedLive = -1;
            s_expectedBlank = -1;
        }

        public static void SetExpectedInitialCounts(int live, int blank)
        {
            s_expectedLive = live;
            s_expectedBlank = blank;
            s_lockedAddress = 0;
        }

        private static IntPtr GetProcessHandle(Process process)
        {
            if (s_cachedPid == process.Id && s_cachedProcessHandle != IntPtr.Zero)
            {
                return s_cachedProcessHandle;
            }

            if (s_cachedProcessHandle != IntPtr.Zero)
            {
                CloseHandle(s_cachedProcessHandle);
                s_cachedProcessHandle = IntPtr.Zero;
            }

            s_cachedPid = process.Id;
            s_cachedProcessHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, process.Id);
            if (s_cachedProcessHandle == IntPtr.Zero)
            {
                s_cachedProcessHandle = process.Handle;
            }

            return s_cachedProcessHandle;
        }

        public enum HolderReadStatus { Ok, Empty, Invalid }

        /// <summary>
        /// Full heap scan returning every array that looks like a shell sequence.
        /// NOTE: this includes stale/freed buffers and the never-modified 'tempSequence' copy,
        /// so results must NOT be used directly for deduction. Use them only to seed reference tracking.
        /// </summary>
        public static List<CandidateInfo> ScanAllCandidates(Process process)
        {
            var list = new List<CandidateInfo>();
            try
            {
                if (process.HasExited) return list;
                IntPtr hProcess = GetProcessHandle(process);
                ForEachHeapChunk(hProcess, (buf, len, baseAddr) => SearchBufferForSequenceArray(hProcess, buf, len, baseAddr, list));
            }
            catch
            {
                // Ignore transient read failures
            }
            return list.GroupBy(c => c.Result.FoundAddress).Select(g => g.First()).ToList();
        }

        /// <summary>
        /// Finds every 8-byte-aligned heap slot whose value equals one of the target data pointers.
        /// For Godot's CowData this locates the owning Array's internal '_ptr' field, which lets us
        /// follow the live sequenceArray across reallocations (remove_at / shuffle / copy-on-write).
        /// </summary>
        public static Dictionary<ulong, List<ulong>> FindPointerHolders(Process process, ICollection<ulong> targets)
        {
            var result = new Dictionary<ulong, List<ulong>>();
            if (targets.Count == 0) return result;
            var set = new HashSet<ulong>(targets);
            try
            {
                IntPtr hProcess = GetProcessHandle(process);
                ForEachHeapChunk(hProcess, (buf, len, baseAddr) =>
                {
                    var qwords = MemoryMarshal.Cast<byte, ulong>(buf.AsSpan(0, len & ~7));
                    for (int i = 0; i < qwords.Length; i++)
                    {
                        ulong v = qwords[i];
                        if (v == 0 || !set.Contains(v)) continue;
                        ulong holder = baseAddr + (ulong)(i * 8);
                        if (!result.TryGetValue(v, out var l)) result[v] = l = new List<ulong>();
                        if (!l.Contains(holder)) l.Add(holder);
                    }
                });
            }
            catch
            {
                // Ignore transient read failures
            }
            return result;
        }

        /// <summary>
        /// Reads the shell array currently referenced by a pointer holder.
        /// Returns Empty when the array's data pointer is null (Godot frees the buffer when size hits 0).
        /// </summary>
        public static HolderReadStatus ReadViaHolder(Process process, ulong holder, int stride, int ptrOffset, out ShellScanResult? result)
        {
            result = null;
            try
            {
                if (process.HasExited) return HolderReadStatus.Invalid;
                IntPtr hProcess = GetProcessHandle(process);

                byte[] pb = new byte[8];
                if (!ReadProcessMemory(hProcess, (IntPtr)holder, pb, 8, out IntPtr r) || r.ToInt64() < 8)
                    return HolderReadStatus.Invalid;

                ulong data = BitConverter.ToUInt64(pb, 0);
                if (data == 0) return HolderReadStatus.Empty;
                if (data < 0x10000 || data > 0x7FFFFFFF0000) return HolderReadStatus.Invalid;

                byte[] buf = new byte[256];
                if (!ReadProcessMemory(hProcess, (IntPtr)(data - 8), buf, buf.Length, out IntPtr rr) || rr.ToInt64() < 16)
                    return HolderReadStatus.Invalid;

                var res = EvaluateAtAddress(hProcess, buf, (int)rr.ToInt64(), data, stride, ptrOffset);
                if (res == null) return HolderReadStatus.Invalid;
                if (res.Sequence.Count == 0) return HolderReadStatus.Empty;

                result = res;
                return HolderReadStatus.Ok;
            }
            catch
            {
                return HolderReadStatus.Invalid;
            }
        }

        private static void ForEachHeapChunk(IntPtr hProcess, Action<byte[], int, ulong> onChunk)
        {
            ulong address = 0x10000;
            ulong maxAddress = 0x7FFFFFFF0000;
            int structSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64));
            const int chunkSize = 1024 * 1024 * 4; // 4MB chunks
            const int overlap = 256;

            while (address < maxAddress)
            {
                if (VirtualQueryEx(hProcess, (IntPtr)address, out MEMORY_BASIC_INFORMATION64 memInfo, (uint)structSize) == 0)
                    break;

                if (memInfo.State == MEM_COMMIT &&
                    (memInfo.Protect == PAGE_READWRITE || memInfo.Protect == PAGE_EXECUTE_READWRITE) &&
                    memInfo.RegionSize >= 4096 &&
                    memInfo.RegionSize <= 1024 * 1024 * 64)
                {
                    ulong regionEnd = memInfo.BaseAddress + memInfo.RegionSize;
                    ulong chunkStart = memInfo.BaseAddress;

                    while (chunkStart < regionEnd)
                    {
                        int toRead = (int)Math.Min((ulong)chunkSize, regionEnd - chunkStart);
                        byte[] buffer = new byte[toRead];
                        if (ReadProcessMemory(hProcess, (IntPtr)chunkStart, buffer, toRead, out IntPtr bytesRead) && bytesRead.ToInt64() >= 32)
                        {
                            onChunk(buffer, (int)bytesRead.ToInt64(), chunkStart);
                        }
                        if (toRead < chunkSize) break;
                        chunkStart += (ulong)(chunkSize - overlap);
                    }
                }

                address = memInfo.BaseAddress + memInfo.RegionSize;
            }
        }

        /// <summary>
        /// Evaluates a locked CowData buffer at a known address.
        /// </summary>
        private static ShellScanResult? EvaluateAtAddress(IntPtr hProcess, byte[] buffer, int length, ulong arrayBase, int stride, int ptrOffset)
        {
            if (length < 16) return null;

            // In Godot 4 CowData:
            // buffer[0..3] = refcount (uint32)
            // buffer[4..7] = size (uint32)
            uint refcount = BitConverter.ToUInt32(buffer, 0);
            uint size = BitConverter.ToUInt32(buffer, 4);

            // Refcount must be valid and non-zero; if refcount == 0, Godot has freed/orphaned this buffer
            if (refcount == 0 || refcount > 64) return null;

            if (size == 0)
            {
                return new ShellScanResult(0, 0, new List<string>(), arrayBase);
            }

            if (size > 8) return null; // Shotgun max capacity is 8 shells

            List<string> sequence = new List<string>((int)size);
            int liveCount = 0;
            int blankCount = 0;

            for (int i = 0; i < (int)size; i++)
            {
                int elemOffset = 8 + i * stride;
                if (elemOffset + stride > length) return null;

                if (stride == 24 && ptrOffset == 8)
                {
                    uint type = BitConverter.ToUInt32(buffer, elemOffset);
                    if (type != 4) return null;
                }
                else if (stride == 24 && ptrOffset == 0)
                {
                    uint type = BitConverter.ToUInt32(buffer, elemOffset + 16);
                    if (type != 4) return null;
                }

                ulong strPtr = BitConverter.ToUInt64(buffer, elemOffset + ptrOffset);
                string? shell = DereferenceShellString(hProcess, strPtr, null, 0);

                if (shell == "LIVE")
                {
                    sequence.Add("LIVE");
                    liveCount++;
                }
                else if (shell == "BLANK")
                {
                    sequence.Add("BLANK");
                    blankCount++;
                }
                else
                {
                    return null;
                }
            }

            return new ShellScanResult(liveCount, blankCount, sequence, arrayBase);
        }

        private static readonly (int Stride, int PtrOffset)[] s_configurations = new (int, int)[]
        {
            (24, 8), // Standard Godot 4.0/4.1+ x64 (Type=STRING at 0, padding at 4, ptr at 8)
            (24, 0), // Alternative layout (ptr at 0, Type=STRING at 16)
            (20, 4), // 32-bit or compact variant
            (20, 0),
            (16, 8),
            (16, 0),
            (8, 0)   // Raw pointer array
        };

        /// <summary>
        /// Searches a memory buffer for Godot 4's CowData sequenceArray containing 'live' and 'blank' variants.
        /// </summary>
        private static void SearchBufferForSequenceArray(IntPtr hProcess, byte[] buffer, int length, ulong pageBase, List<CandidateInfo> candidates)
        {
            for (int offset = 0; offset <= length - 32; offset += 4)
            {
                uint refcount = BitConverter.ToUInt32(buffer, offset);
                uint size = BitConverter.ToUInt32(buffer, offset + 4);

                // Buckshot shotgun load is between 1 and 8 shells (initial load >= 2, down to 1 during round)
                if (size < 1 || size > 8 || refcount == 0 || refcount > 64)
                {
                    continue;
                }

                foreach (var (stride, ptrOffset) in s_configurations)
                {
                    int totalArrayBytes = 8 + (int)size * stride;
                    if (offset + totalArrayBytes > length) continue;

                    int elem0 = offset + 8;
                    if (stride == 24 && ptrOffset == 8)
                    {
                        uint type0 = BitConverter.ToUInt32(buffer, elem0);
                        if (type0 != 4) continue;
                    }
                    else if (stride == 24 && ptrOffset == 0)
                    {
                        uint type0 = BitConverter.ToUInt32(buffer, elem0 + 16);
                        if (type0 != 4) continue;
                    }

                    ulong ptr0 = BitConverter.ToUInt64(buffer, elem0 + ptrOffset);
                    if (ptr0 < 0x10000 || ptr0 > 0x7FFFFFFF0000) continue;

                    string? shell0 = DereferenceShellString(hProcess, ptr0, buffer, pageBase);
                    if (shell0 == null) continue;

                    // First element is a valid shell! Check all remaining elements
                    List<string> sequence = new List<string>((int)size) { shell0 };
                    int live = shell0 == "LIVE" ? 1 : 0;
                    int blank = shell0 == "BLANK" ? 1 : 0;
                    bool allMatch = true;

                    for (int i = 1; i < (int)size; i++)
                    {
                        int elemI = offset + 8 + i * stride;
                        if (stride == 24 && ptrOffset == 8)
                        {
                            uint typeI = BitConverter.ToUInt32(buffer, elemI);
                            if (typeI != 4) { allMatch = false; break; }
                        }
                        else if (stride == 24 && ptrOffset == 0)
                        {
                            uint typeI = BitConverter.ToUInt32(buffer, elemI + 16);
                            if (typeI != 4) { allMatch = false; break; }
                        }

                        ulong ptrI = BitConverter.ToUInt64(buffer, elemI + ptrOffset);
                        string? shellI = DereferenceShellString(hProcess, ptrI, buffer, pageBase);
                        if (shellI == "LIVE")
                        {
                            sequence.Add("LIVE");
                            live++;
                        }
                        else if (shellI == "BLANK")
                        {
                            sequence.Add("BLANK");
                            blank++;
                        }
                        else
                        {
                            allMatch = false;
                            break;
                        }
                    }

                    if (allMatch && sequence.Count == (int)size)
                    {
                        ulong foundAddress = pageBase + (ulong)(offset + 8);
                        candidates.Add(new CandidateInfo(new ShellScanResult(live, blank, sequence, foundAddress), stride, ptrOffset));
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Inspects the memory at ptr to determine if it contains 'live' or 'blank' (UTF-32, ASCII, or StringName).
        /// </summary>
        private static string? DereferenceShellString(IntPtr hProcess, ulong ptr, byte[]? pageBuf, ulong pageBase)
        {
            if (ptr < 0x10000 || ptr > 0x7FFFFFFF0000) return null;

            byte[] strBuf = new byte[32];

            // Fast path: if pointer falls inside currently buffered page, read directly from byte array
            if (pageBuf != null && ptr >= pageBase && ptr + 32 <= pageBase + (ulong)pageBuf.Length)
            {
                int localOffset = (int)(ptr - pageBase);
                Buffer.BlockCopy(pageBuf, localOffset, strBuf, 0, 32);
            }
            else
            {
                if (!ReadProcessMemory(hProcess, (IntPtr)ptr, strBuf, strBuf.Length, out IntPtr bytesRead) || bytesRead.ToInt32() < 16)
                {
                    return null;
                }
            }

            // Direct check at offset 0
            string? match = MatchShellBytes(strBuf, 0);
            if (match != null) return match;

            // Check at offset 8 (if ptr points to CowData allocation header instead of characters)
            match = MatchShellBytes(strBuf, 8);
            if (match != null) return match;

            // Check indirect pointer (pointer to String / StringName)
            ulong indirectPtr = BitConverter.ToUInt64(strBuf, 0);
            if (indirectPtr >= 0x10000 && indirectPtr < 0x7FFFFFFF0000 && indirectPtr != ptr)
            {
                byte[] indirectBuf = new byte[32];
                if (ReadProcessMemory(hProcess, (IntPtr)indirectPtr, indirectBuf, indirectBuf.Length, out IntPtr indRead) && indRead.ToInt32() >= 16)
                {
                    match = MatchShellBytes(indirectBuf, 0);
                    if (match != null) return match;
                    match = MatchShellBytes(indirectBuf, 8);
                    if (match != null) return match;
                }
            }

            return null;
        }

        private static string? MatchShellBytes(byte[] buf, int offset)
        {
            if (offset + 16 > buf.Length) return null;

            // 1. Godot 4 UTF-32 LE 'live' or 'LIVE'
            if ((buf[offset] == 0x6C || buf[offset] == 0x4C) && buf[offset + 1] == 0 && buf[offset + 2] == 0 && buf[offset + 3] == 0 &&
                (buf[offset + 4] == 0x69 || buf[offset + 4] == 0x49) && buf[offset + 5] == 0 && buf[offset + 6] == 0 && buf[offset + 7] == 0 &&
                (buf[offset + 8] == 0x76 || buf[offset + 8] == 0x56) && buf[offset + 9] == 0 && buf[offset + 10] == 0 && buf[offset + 11] == 0 &&
                (buf[offset + 12] == 0x65 || buf[offset + 12] == 0x45) && buf[offset + 13] == 0 && buf[offset + 14] == 0 && buf[offset + 15] == 0)
            {
                return "LIVE";
            }

            // 2. Godot 4 UTF-32 LE 'blank' or 'BLANK'
            if (offset + 20 <= buf.Length &&
                (buf[offset] == 0x62 || buf[offset] == 0x42) && buf[offset + 1] == 0 && buf[offset + 2] == 0 && buf[offset + 3] == 0 &&
                (buf[offset + 4] == 0x6C || buf[offset + 4] == 0x4C) && buf[offset + 5] == 0 && buf[offset + 6] == 0 && buf[offset + 7] == 0 &&
                (buf[offset + 8] == 0x61 || buf[offset + 8] == 0x41) && buf[offset + 9] == 0 && buf[offset + 10] == 0 && buf[offset + 11] == 0 &&
                (buf[offset + 12] == 0x6E || buf[offset + 12] == 0x4E) && buf[offset + 13] == 0 && buf[offset + 14] == 0 && buf[offset + 15] == 0 &&
                (buf[offset + 16] == 0x6B || buf[offset + 16] == 0x4B) && buf[offset + 17] == 0 && buf[offset + 18] == 0 && buf[offset + 19] == 0)
            {
                return "BLANK";
            }

            // 3. ASCII / UTF-8 'live\0' or 'LIVE\0'
            if (offset + 5 <= buf.Length &&
                (buf[offset] == 0x6C || buf[offset] == 0x4C) &&
                (buf[offset + 1] == 0x69 || buf[offset + 1] == 0x49) &&
                (buf[offset + 2] == 0x76 || buf[offset + 2] == 0x56) &&
                (buf[offset + 3] == 0x65 || buf[offset + 3] == 0x45) &&
                buf[offset + 4] == 0)
            {
                return "LIVE";
            }

            // 4. ASCII / UTF-8 'blank\0' or 'BLANK\0'
            if (offset + 6 <= buf.Length &&
                (buf[offset] == 0x62 || buf[offset] == 0x42) &&
                (buf[offset + 1] == 0x6C || buf[offset + 1] == 0x4C) &&
                (buf[offset + 2] == 0x61 || buf[offset + 2] == 0x41) &&
                (buf[offset + 3] == 0x6E || buf[offset + 3] == 0x4E) &&
                (buf[offset + 4] == 0x6B || buf[offset + 4] == 0x4B) &&
                buf[offset + 5] == 0)
            {
                return "BLANK";
            }

            return null;
        }
    }
}
