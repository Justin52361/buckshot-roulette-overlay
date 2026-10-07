using System;
using System.IO;

namespace BuckshotOverlay
{
    /// <summary>
    /// Programmatically generates a 100% valid, native 64-bit Windows PE DLL (BuckshotHook.dll).
    /// This DLL is injected into the 64-bit Buckshot Roulette.exe process.
    /// When LoadLibraryW loads it, DllMain executes with DLL_PROCESS_ATTACH.
    /// </summary>
    public static class DllGenerator
    {
        public static string EnsureDllExists(string targetFolder)
        {
            Directory.CreateDirectory(targetFolder);
            string dllPath = Path.Combine(targetFolder, "BuckshotHook.dll");

            byte[] dllBytes = Generate64BitDll();
            File.WriteAllBytes(dllPath, dllBytes);
            return dllPath;
        }

        public static byte[] Generate64BitDll()
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                // --- DOS HEADER (64 bytes) ---
                bw.Write((ushort)0x5A4D); // 'MZ'
                for (int i = 0; i < 29; i++) bw.Write((ushort)0);
                bw.Write((uint)0x80); // e_lfanew = 0x80

                // --- DOS STUB (64 bytes) ---
                byte[] stub = new byte[] {
                    0x0E, 0x1F, 0xBA, 0x0E, 0x00, 0xB4, 0x09, 0xCD,
                    0x21, 0xB8, 0x01, 0x4C, 0xCD, 0x21, 0x54, 0x68,
                    0x69, 0x73, 0x20, 0x70, 0x72, 0x6F, 0x67, 0x72,
                    0x61, 0x6D, 0x20, 0x63, 0x61, 0x6E, 0x6E, 0x6F,
                    0x74, 0x20, 0x62, 0x65, 0x20, 0x72, 0x75, 0x6E,
                    0x20, 0x69, 0x6E, 0x20, 0x44, 0x4F, 0x53, 0x20,
                    0x6D, 0x6F, 0x64, 0x65, 0x2E, 0x0D, 0x0D, 0x0A,
                    0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
                };
                bw.Write(stub);

                // --- PE SIGNATURE (4 bytes at 0x80) ---
                bw.Write((uint)0x00004550); // "PE\0\0"

                // --- COFF FILE HEADER (20 bytes) ---
                bw.Write((ushort)0x8664); // Machine = AMD64 (x64)
                bw.Write((ushort)1);      // NumberOfSections = 1 (.text)
                bw.Write((uint)0x66F00000); // TimeDateStamp
                bw.Write((uint)0);        // PointerToSymbolTable
                bw.Write((uint)0);        // NumberOfSymbols
                bw.Write((ushort)240);    // SizeOfOptionalHeader
                bw.Write((ushort)0x2022); // Characteristics: EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE | DLL

                // --- OPTIONAL HEADER (PE32+ 64-bit) (240 bytes) ---
                bw.Write((ushort)0x20B);  // Magic = PE32+
                bw.Write((byte)14);       // MajorLinkerVersion
                bw.Write((byte)0);        // MinorLinkerVersion
                bw.Write((uint)0x200);    // SizeOfCode
                bw.Write((uint)0);        // SizeOfInitializedData
                bw.Write((uint)0);        // SizeOfUninitializedData
                bw.Write((uint)0x1000);   // AddressOfEntryPoint (.text section RVA)
                bw.Write((uint)0x1000);   // BaseOfCode
                bw.Write((ulong)0x180000000); // ImageBase
                bw.Write((uint)0x1000);   // SectionAlignment (4096)
                bw.Write((uint)0x200);    // FileAlignment (512)
                bw.Write((ushort)6);      // MajorOSVersion
                bw.Write((ushort)0);      // MinorOSVersion
                bw.Write((ushort)0);      // MajorImageVersion
                bw.Write((ushort)0);      // MinorImageVersion
                bw.Write((ushort)6);      // MajorSubsystemVersion
                bw.Write((ushort)0);      // MinorSubsystemVersion
                bw.Write((uint)0);        // Win32VersionValue
                bw.Write((uint)0x2000);   // SizeOfImage
                bw.Write((uint)0x200);    // SizeOfHeaders
                bw.Write((uint)0);        // CheckSum
                bw.Write((ushort)2);      // Subsystem = WINDOWS_GUI
                bw.Write((ushort)0x8160); // DllCharacteristics: DYNAMIC_BASE | NX_COMPAT | TERMINAL_SERVER_AWARE | HIGH_ENTROPY_VA
                bw.Write((ulong)0x100000);// SizeOfStackReserve
                bw.Write((ulong)0x1000);  // SizeOfStackCommit
                bw.Write((ulong)0x100000);// SizeOfHeapReserve
                bw.Write((ulong)0x1000);  // SizeOfHeapCommit
                bw.Write((uint)0);        // LoaderFlags
                bw.Write((uint)16);       // NumberOfRvaAndSizes

                // 16 Data Directories (128 bytes)
                for (int i = 0; i < 16; i++)
                {
                    bw.Write((uint)0); // RVA
                    bw.Write((uint)0); // Size
                }

                // --- SECTION HEADER: .text (40 bytes) ---
                byte[] secName = new byte[8];
                secName[0] = (byte)'.'; secName[1] = (byte)'t'; secName[2] = (byte)'e'; secName[3] = (byte)'x'; secName[4] = (byte)'t';
                bw.Write(secName);
                bw.Write((uint)0x40);     // VirtualSize
                bw.Write((uint)0x1000);   // VirtualAddress
                bw.Write((uint)0x200);    // SizeOfRawData
                bw.Write((uint)0x200);    // PointerToRawData
                bw.Write((uint)0);        // PointerToRelocations
                bw.Write((uint)0);        // PointerToLinenumbers
                bw.Write((ushort)0);      // NumberOfRelocations
                bw.Write((ushort)0);      // NumberOfLinenumbers
                bw.Write((uint)0x60000020); // Characteristics: IMAGE_SCN_CNT_CODE | IMAGE_SCN_MEM_EXECUTE | IMAGE_SCN_MEM_READ

                // Pad headers to 0x200 (FileAlignment)
                while (ms.Position < 0x200) bw.Write((byte)0);

                // --- .text CODE (Starts at 0x200) ---
                // Native x64 DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpReserved):
                // 48 83 EC 28         sub rsp, 28h
                // 83 FA 01            cmp edx, 1        ; check if DLL_PROCESS_ATTACH
                // 75 02               jne skip
                // 90 90               nop nop
                // skip:
                // B8 01 00 00 00      mov eax, 1        ; return TRUE
                // 48 83 C4 28         add rsp, 28h
                // C3                  ret
                byte[] code = new byte[] {
                    0x48, 0x83, 0xEC, 0x28,
                    0x83, 0xFA, 0x01,
                    0x75, 0x02,
                    0x90, 0x90,
                    0xB8, 0x01, 0x00, 0x00, 0x00,
                    0x48, 0x83, 0xC4, 0x28,
                    0xC3
                };
                bw.Write(code);

                // Pad .text to 0x400
                while (ms.Position < 0x400) bw.Write((byte)0);

                return ms.ToArray();
            }
        }
    }
}
