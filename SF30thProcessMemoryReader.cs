using Gapotchenko.FX.Diagnostics;
using ManagedWinapi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SF30thPlayerReader
{
    public class SF30thProcessMemoryReader
    {
        #region Fields
        // Cache user's steam info here, which we will be searching for in process memory.
        private int _steamID3;
        private string _steamUser;

        /// <summary>
        /// The byte array to search for that identifies the memory block containing player names.
        /// The first 4 bytes will be replaced with _steamID3.
        /// </summary>
        private readonly byte[] _signature = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x10, 0x01 };

        /// <summary>
        /// Cached address of memory block containing player names.
        /// </summary>
        private long _namesAddress;
        #endregion

        #region Properties
        /// <summary>
        /// To prevent another search from executing when one is already running.
        /// </summary>
        public bool IsBusy { get; private set; }
        #endregion

        #region Methods
        /// <summary>
        /// Reads lobby player names from SF30thAnniversaryCollection process memory.
        /// </summary>
        /// <returns>List of player names.</returns>
        public List<string> ReadPlayerNames()
        {
            if (IsBusy)
                return null;

            IsBusy = true;

            try
            {
                Debug.WriteLine("Reading player names...");

                // Open game process.
                var processName = "SF30thAnniversaryCollection";
                using var process = Process.GetProcessesByName(processName).FirstOrDefault();

                if (process == null)
                {
                    Trace.WriteLine($"Process not found: {processName}", "Warning");
                    return null;
                }

                // Get Steam user info that we will be searching for.
                var steamID3 = GetSteamID3(process);
                UpdateSignature(steamID3);

                var address = FindPlayerNamesAddress(process);

                if (address <= 0)
                {
                    Trace.WriteLine("Player names not found.", "Error");
                    return null;
                }

                var names = ReadPlayerNames(process, address);

                if (string.IsNullOrWhiteSpace(names[0]))
                {
                    Trace.WriteLine("Memory address changed.");
                    // Reset to 0 so a new scan is performed next call.
                    _namesAddress = 0;
                }

                return names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            }
            catch (Exception e)
            {
                Trace.WriteLine(e.Message, "Error");
                return null;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Reads lobby player names from process memory.
        /// </summary>
        /// <param name="process">The process to read from.</param>
        /// <param name="address">The location in memory to start reading.</param>
        /// <returns>A collection of player names.</returns>
        private string[] ReadPlayerNames(Process process, long address)
        {
            var names = new string[4];

            for (var i = 0; i < names.Length; ++i)
            {
                names[i] = ReadString(process, address);
                address += 96;
            }

            return names;
        }

        /// <summary>
        /// Searches for starting address of memory block containing player names.
        /// </summary>
        /// <param name="process">The process to search.</param>
        /// <returns></returns>
        private long FindPlayerNamesAddress(Process process)
        {
            // If we have already found the address, no need to look for it again.
            if (_namesAddress > 0)
                return _namesAddress;

            Trace.WriteLine("Searching for memory address of player names...");

            var maxAddress = 0xfffffffffff;
            var address = 0L;
            var tasks = new List<Task>();
            var cts = new CancellationTokenSource();

            do
            {
                if (VirtualQueryEx(process.Handle, (IntPtr)address, out var memInfo, (uint)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64))) == 0)
                    throw new Exception("Failed to retrieve page info.");

                Debug.WriteLine($"Block: [{memInfo.BaseAddress}-{memInfo.BaseAddress + memInfo.RegionSize - 1}] Size: {memInfo.RegionSize} | State: {(MemState)memInfo.State} | Protect: {(AllocationProtect)memInfo.Protect} | Type: {(MemType)memInfo.Type}");

                if (address == (long)(memInfo.BaseAddress + memInfo.RegionSize))
                    break;

                address = (long)(memInfo.BaseAddress + memInfo.RegionSize);

                // Only search these pages.
                if ((MemState)memInfo.State == MemState.MEM_COMMIT
                    && (AllocationProtect)memInfo.Protect == AllocationProtect.PAGE_READWRITE
                    && (MemType)memInfo.Type == MemType.MEM_PRIVATE)
                {
                    var task = Task.Run(() =>
                    {
                        var address = FindPlayerNamesAddress(process, new long[] {
                            (long)memInfo.BaseAddress,
                            (long)(memInfo.BaseAddress + memInfo.RegionSize - 1) });

                        if (address > -1)
                        {
                            Trace.WriteLine($"Address found: {address}");
                            _namesAddress = address;
                            cts.Cancel();
                        }
                    }, cts.Token);
                    tasks.Add(task);
                }
            } while (address <= maxAddress && _namesAddress == 0);

            try
            {
                Task.WaitAll(tasks.ToArray());
            }
            catch (AggregateException e1)
            {
                foreach (var e2 in e1.InnerExceptions.Where(e => !(e is OperationCanceledException)))
                    Trace.WriteLine(e2.Message, "Error");
            }

            return _namesAddress;
        }

        /// <summary>
        /// Searches for starting address of memory block containing player names.
        /// </summary>
        /// <param name="process">The process to search.</param>
        /// <param name="addressRange">The range of memory within process to search.</param>
        /// <returns></returns>
        private long FindPlayerNamesAddress(Process process, long[] addressRange)
        {
            // Stop looking if already found by another task.
            if (_namesAddress > 0)
                return -1;

            Debug.WriteLine($"Searching for player names in block: [{addressRange[0]}-{addressRange[1]}]");
            var chunkSize = 16;

            for (long address = addressRange[0]; address + chunkSize < addressRange[1] && _namesAddress <= 0; address += chunkSize)
            {
                var memChunk = new ProcessMemoryChunk(process, (IntPtr)address, chunkSize);
                var buf = memChunk.Read();

                // Signature found!
                if (_signature.SequenceEqual(buf.Take(8)) && buf[8] != 0x00)
                {
                    // Make sure Steam username follows, otherwise continue searching.
                    var name = ReadString(process, address + 8);

                    if (name != _steamUser)
                        continue;

                    // Backtrack to beginning of names block.
                    var startAddress = address + 8;
                    var signature = _signature.Skip(4);
                    address -= 92;

                    for (var x = 0; x < 3 && address > addressRange[0]; ++x, address -= 96)
                    {
                        memChunk = new ProcessMemoryChunk(process, (IntPtr)address, 4);
                        buf = memChunk.Read();

                        // Found another name. Continue backtracking...
                        if (signature.SequenceEqual(buf) && (name = ReadString(process, address + 4)) != null && name.Length >= 2 && name.Length <= 32)
                        {
                            startAddress = address + 4;
                            continue;
                        }

                        // Did not find another name. We have found the beginning of the names block.
                        break;
                    }

                    return startAddress;
                }
            }

            return -1;
        }

        /// <summary>
        /// Reads a null-terminated string from process memory.
        /// </summary>
        /// <param name="process"></param>
        /// <param name="address"></param>
        /// <returns></returns>
        private string ReadString(Process process, long address)
        {
            var bytes = new byte[32];

            if (!ReadProcessMemory(process.Handle, (IntPtr)address, bytes, bytes.Length, out var _))
            {
                Trace.WriteLine($"Failed to read string from address: {address}", "Error");
                return null;
            }

            var strBytes = bytes.Take(bytes.ToList().IndexOf(0));
            var str = Encoding.UTF8.GetString(strBytes.ToArray());

            if (!Regex.IsMatch(str, @"^[\w\s0-9]+$"))
                return null;

            return str;
        }

        private int GetSteamID3(Process process)
        {
            if (_steamID3 > 0)
                return _steamID3;

            var environmentVars = process.ReadEnvironmentVariables();
            var steamID64var = environmentVars["STEAMID"];

            if (steamID64var == null)
                throw new Exception("Cannot find STEAMID.");

            var steamID64 = long.Parse(steamID64var);
            var steamID3 = steamID64 - 76561197960265728;
            var steamUser = environmentVars["SteamUser"];
            _steamUser = steamUser;
            Trace.WriteLine($"SteamUser: {steamUser} | SteamID3: {steamID3}");
            _steamID3 = (int)steamID3;
            return _steamID3;
        }

        private void UpdateSignature(int steamID3)
        {
            var buf = BitConverter.GetBytes(steamID3);

            for (var i = 0; i < 4; ++i)
                _signature[i] = buf[i];
        }
        #endregion

        #region Win32 Imports
        private enum AllocationProtect : uint
        {
            PAGE_EXECUTE = 0x00000010,
            PAGE_EXECUTE_READ = 0x00000020,
            PAGE_EXECUTE_READWRITE = 0x00000040,
            PAGE_EXECUTE_WRITECOPY = 0x00000080,
            PAGE_NOACCESS = 0x00000001,
            PAGE_READONLY = 0x00000002,
            PAGE_READWRITE = 0x00000004,
            PAGE_WRITECOPY = 0x00000008,
            PAGE_GUARD = 0x00000100,
            PAGE_NOCACHE = 0x00000200,
            PAGE_WRITECOMBINE = 0x00000400
        }

        private enum MemState
        {
            MEM_COMMIT = 0x1000,
            MEM_RESERVER = 0x2000,
            MEM_FREE = 0x10000,
        }

        private enum MemType
        {
            MEM_PRIVATE = 0x20000,
            MEM_MAPPED = 0x40000,
            MEM_IMAGE = 0x1000000,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION64
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public int AllocationProtect;
            public int __alignment1;
            public ulong RegionSize;
            public int State;
            public int Protect;
            public int Type;
            public int __alignment2;
        }

        [DllImport("kernel32.dll")]
        static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION64 lpBuffer, uint dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
           IntPtr processHandle,
           IntPtr address,
           [Out] byte[] outBuffer,
           int bytesToRead,
           out IntPtr bytesRead);
        #endregion
    }
}