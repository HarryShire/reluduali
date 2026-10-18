using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BatchRecorder
{
    /// <summary>
    /// A view of Redacted's replay clock, read-only except where stated.
    ///
    /// **Writing is allowed against Redacted and forbidden against retail.**
    /// Mycelium's rule (2026-09-29): `t6mpv43.exe` is a modding client, offline and
    /// unconnected to Steam, so its memory is fair game; retail `t6mp.exe` is
    /// strictly read-only. That is enforced in <see cref="Attach"/>, which only
    /// asks for write access when the process it found is named `t6mpv43`, and in
    /// every write, which refuses if the writable handle was never opened. A rule
    /// this consequential belongs in code, not in someone's memory.
    ///
    /// Addresses come from the host's own table when it has one, and otherwise
    /// from the Redacted values below.
    /// Dvars are found by walking the game's own dvar table, so no host is needed.
    ///
    /// The clock does NOT start at zero. Ticks keep counting through the load
    /// screen and the menu and theater never resets them, so the
    /// zero point differs per load. <see cref="MeasureZero"/> is how it is found,
    /// and nothing should convert a clock time to a tick without it.
    /// </summary>
    public sealed class GameClock : IDisposable
    {
        private const int PROCESS_VM_READ = 0x0010;
        private const int PROCESS_VM_WRITE = 0x0020;
        private const int PROCESS_VM_OPERATION = 0x0008;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;

        /// <summary>The only process this class will ever write to.</summary>
        private const string WritableProcess = "t6mpv43";

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr written);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, UIntPtr size, uint newProtect, out uint oldProtect);
        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, UIntPtr size);

        // Redacted's (t6mpv43). Used only when the host has not resolved its own
        // addresses.
        private const int TickOffset = 0x00D54520;
        private const int EndTickOffset = 0x02CF3470;
        private const int TheaterBaseOffset = 0x02D83A00;
        private const int PlayerIndexOffset = 0x0264C840;


        /// <summary>Jump points sit on a 10 second grid, whatever the zero is.</summary>
        public const int GridSpacing = 10000;

        private IntPtr _handle;
        private IntPtr _writeHandle;
        private IntPtr _base;
        private string _processName;
        private int _pid;

        [DllImport("kernel32.dll")]
        private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        private const uint STILL_ACTIVE = 259;

        public string Error { get; private set; }
        public bool Attached { get { return _handle != IntPtr.Zero; } }

        /// <summary>
        /// The game this clock reads is still running. A restarted game is a new process
        /// at new addresses, and every read of the old handle quietly fails, so "attached"
        /// alone is not enough: the tether has to notice and re-attach.
        /// </summary>
        public bool Alive
        {
            get
            {
                uint code;
                return _handle != IntPtr.Zero && GetExitCodeProcess(_handle, out code) && code == STILL_ACTIVE;
            }
        }

        /// <summary>The game's process id, or 0.</summary>
        public int ProcessId { get { return Attached ? _pid : 0; } }

        public bool Attach()
        {
            Dispose();
            lock (_dvars) { _dvars.Clear(); _dvarScan = DateTime.MinValue; }   // a new game, new addresses
            Process p = null;
            foreach (string name in new[] { "t6mpv43" })                    // Redacted only (the code review's C-20)
            {
                Process[] found = Process.GetProcessesByName(name);
                if (found.Length > 0) { p = found[0]; break; }
            }
            if (p == null) { Error = "Redacted (t6mpv43) is not running."; return false; }

            _base = p.MainModule.BaseAddress;
            _processName = p.ProcessName;
            _pid = p.Id;
            _handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, p.Id);
            if (_handle == IntPtr.Zero) { Error = "Could not open the game for reading: " + Marshal.GetLastWin32Error(); return false; }

            // A SECOND handle, and only for Redacted. Retail t6mp is never opened
            // for writing, so no code path below can reach it however it is called.
            if (string.Equals(_processName, WritableProcess, StringComparison.OrdinalIgnoreCase))
                _writeHandle = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION,
                                           false, p.Id);

            Error = "";
            return true;
        }

        /// <summary>True when this is Redacted and writing is permitted.</summary>
        public bool CanWrite { get { return _writeHandle != IntPtr.Zero; } }

        /// <summary>The process being read, for anything that wants to say so out loud.</summary>
        public string ProcessName { get { return _processName ?? ""; } }

        /// <summary>
        /// Writes bytes into Redacted. Refuses outright when the writable handle
        /// was never opened, which is every case except `t6mpv43`.
        /// </summary>
        private bool Write(IntPtr address, byte[] data)
        {
            if (_writeHandle == IntPtr.Zero)
            {
                Error = "writing is only allowed against Redacted (t6mpv43); this is '" + ProcessName + "'";
                return false;
            }
            IntPtr written;
            if (!WriteProcessMemory(_writeHandle, address, data, data.Length, out written) || written.ToInt32() != data.Length)
            {
                Error = "write failed at 0x" + address.ToInt64().ToString("X") + ": " + Marshal.GetLastWin32Error();
                return false;
            }
            Error = "";
            return true;
        }

        /// <summary>
        /// Sets the spectated player directly. `demo_client n` does this properly
        /// through the game's own path and is preferred; this is for when a command
        /// cannot be sent, and for testing whether the view follows a bare write.
        /// </summary>
        public bool SetPlayerIndex(int slot)
        {
            if (slot < 0 || slot >= RosterSlots) { Error = "slot out of range"; return false; }
            return Write(Resolve("selectedPlayerIndex", PlayerIndexOffset), BitConverter.GetBytes(slot));
        }

        /// <summary>
        /// Sets or clears the CODCAST bit in the demo-mode byte.
        ///
        /// UNPROVEN, and the reason is worth stating: the menu that normally sets
        /// this mode does other work besides flipping a bit, so a bare write may
        /// give a half-dressed state - the flag on, the HUD not. The test is
        /// whether the screen actually changes, not whether the bit reads back.
        /// </summary>
        public bool SetCodcast(bool on)
        {
            int flags = DemoModeFlags;
            if (flags < 0) { Error = "could not read the demo mode byte"; return false; }
            byte wanted = (byte)(on ? (flags | CodcastBit) : (flags & ~CodcastBit));
            return Write(new IntPtr(_base.ToInt64() + DemoModeOffset), new byte[] { wanted });
        }


        private int ReadInt(IntPtr address)
        {
            byte[] buf = new byte[4];
            IntPtr got;
            if (!ReadProcessMemory(_handle, address, buf, 4, out got) || got.ToInt32() != 4) return int.MinValue;
            return BitConverter.ToInt32(buf, 0);
        }

        /// <summary>
        /// Raw bytes from the game, or null when any of them cannot be read. Read only, like the rest of this class:
        /// the CAM QUEUE's marker read and its 3D view (2026-10-14) take the theater's dolly markers through it.
        /// </summary>
        public byte[] ReadBytes(long address, int count)
        {
            if (_handle == IntPtr.Zero || count <= 0 || address <= 0) return null;
            byte[] buf = new byte[count];
            IntPtr got;
            if (!ReadProcessMemory(_handle, new IntPtr(address), buf, count, out got) || got.ToInt32() != count) return null;
            return buf;
        }

        /// <summary>The game module's base (0x400000 for t6mpv43, which has no ASLR), or 0 when not attached.</summary>
        public long ModuleBaseAddress { get { return _handle == IntPtr.Zero ? 0 : _base.ToInt64(); } }

        private IntPtr Resolve(string hostField, int fallbackOffset)
        {
            IntPtr fromHost = ConsoleHost.Current.GetAddress(hostField);
            if (fromHost != IntPtr.Zero) return fromHost;
            return new IntPtr(_base.ToInt64() + fallbackOffset);
        }

        // The game's dvar table: a fixed array of 0x60-byte records inside the module,
        // the name pointer at +0, the type at +0x10, the value at +0x18. RipperBridge
        // walks the same table in-process (kRvaDvarPool, RipperBridge.cpp); walking it
        // from outside is what lets the batch run without a host (Stage 1, 2026-10-01). It
        // ends at the first record whose name cannot be read.
        private const int DvarPoolOffset = 0x261FC48;
        private const int DvarStride = 0x60;
        private const int DvarPoolMax = 6000;
        private readonly Dictionary<string, IntPtr> _dvars = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        private DateTime _dvarScan = DateTime.MinValue;

        /// <summary>
        /// A dvar_t record by name, or zero. Plugins register dvars late (a plugin's
        /// appear once it loads), so a name not found rescans, at most every 5 seconds.
        /// </summary>
        private IntPtr FindDvar(string name)
        {
            IntPtr d;
            lock (_dvars)
            {
                if (_dvars.TryGetValue(name, out d)) return d;
                if ((DateTime.UtcNow - _dvarScan).TotalSeconds >= 5)
                {
                    ScanDvars();
                    if (_dvars.TryGetValue(name, out d)) return d;
                }
            }
            return ConsoleHost.Current.FindDvarStruct(name);
        }

        private void ScanDvars()
        {
            _dvarScan = DateTime.UtcNow;
            _dvars.Clear();
            if (_handle == IntPtr.Zero) return;

            long pool = _base.ToInt64() + DvarPoolOffset;
            const int perChunk = 256;
            byte[] chunk = new byte[perChunk * DvarStride];
            for (int first = 0; first < DvarPoolMax; first += perChunk)
            {
                IntPtr got;
                if (!ReadProcessMemory(_handle, new IntPtr(pool + (long)first * DvarStride), chunk, chunk.Length, out got)) return;
                int records = got.ToInt32() / DvarStride;
                for (int i = 0; i < records; i++)
                {
                    int namePtr = BitConverter.ToInt32(chunk, i * DvarStride);
                    string name = namePtr == 0 ? null : ReadString(new IntPtr(namePtr), 80);
                    if (string.IsNullOrEmpty(name)) return;
                    if (!_dvars.ContainsKey(name)) _dvars[name] = new IntPtr(pool + (long)(first + i) * DvarStride);
                }
            }
        }

        /// <summary>True when the game has a dvar by this name (the table walk, then the host's finder).</summary>
        public bool HasDvar(string name) { return Attached && FindDvar(name) != IntPtr.Zero; }

        /// <summary>How many dvars the last table walk found, for the tether display.</summary>
        public int DvarCount { get { lock (_dvars) { if (_dvars.Count == 0) ScanDvars(); return _dvars.Count; } } }

        public int Tick { get { return ReadInt(Resolve("Tick", TickOffset)); } }
        public int EndTick { get { return ReadInt(Resolve("EndTick", EndTickOffset)); } }
        public int TheaterBase { get { return ReadInt(Resolve("TheaterBase", TheaterBaseOffset)); } }
        public int PlayerIndex { get { return ReadInt(Resolve("selectedPlayerIndex", PlayerIndexOffset)); } }

        /// <summary>A demo is loaded and the theater is up. EndTick keeps its old value after playback ends, so it is not the signal.</summary>
        public bool InTheater { get { int t = TheaterBase; return t != 0 && t != int.MinValue; } }

        /// <summary>True when two reads a moment apart agree, i.e. playback really is paused.</summary>
        public bool IsStill(int settleMs = 120)
        {
            int a = Tick;
            System.Threading.Thread.Sleep(settleMs);
            return a == Tick && a != int.MinValue;
        }

        /// <summary>
        /// The zero of this load, from where a jump lands. Jump points sit on a
        /// 10 second grid, so the landing's remainder is the offset between tick
        /// and clock. Retail measured 2000; this Redacted load measured 2300 --
        /// hence measuring it rather than assuming.
        /// </summary>
        public static int ZeroFromLanding(int landedTick)
        {
            int z = landedTick % GridSpacing;
            if (z < 0) z += GridSpacing;
            return z;
        }

        public static int TickForClock(int clockSeconds, int zero) { return clockSeconds * 1000 + zero; }
        public static int ClockForTick(int tick, int zero) { return (int)Math.Floor((tick - zero) / 1000.0); }

        /// <summary>Reads an int dvar. +24 is the value, per the game's record layout.</summary>
        public int ReadDvarInt(string name, int fallback = int.MinValue)
        {
            IntPtr d = FindDvar(name);
            if (d == IntPtr.Zero) return fallback;
            int v = ReadInt(new IntPtr(d.ToInt64() + 24));
            return v == int.MinValue ? fallback : v;
        }

        /// <summary>
        /// The spectate roster, found by scanning the running client for a known
        /// gamertag on 2026-09-29 and confirmed three ways: consecutive indices
        /// matched consecutive clicks, and six left-clicks from index 10 landed on
        /// index 4, which is what this array said they would.
        ///
        /// It is INSIDE the module, so the address is fixed across loads and
        /// restarts; only the contents change per match. A second array exists at
        /// module+0xC9C350 (stride 104) carrying clan tags, but it has holes in it
        /// and this one does not.
        ///
        /// This is indexed by selectedPlayerIndex. The scoreboard-ordered array at
        /// module+0xD49338 is NOT -- it looks like a roster and lies, which is the
        /// whole reason POV mapping took until two in the morning.
        /// </summary>
        public const int RosterOffset = 0x2D6D8DC;

        /// <summary>
        /// The demo-mode bitfield. **Bit 2 is CODCAST** - the game calls it
        /// shoutcaster; the Polish menu calls it FILM CODCAST.
        ///
        /// Found 2026-09-29 by reading the code behind the game's own
        /// `IsDemoShoutcaster` script function, NOT by diffing memory: diffing
        /// regular against codcast gave 1.3 million candidate bytes and nothing
        /// usable, because changing mode reloads the demo and everything moves.
        /// The registration site `push 0x009772A0; push "IsDemoShoutcaster"` gave
        /// the function, and the predicate it calls ends:
        ///
        ///     movzx eax, byte [0x030D3408]   ; shr eax, 2 ; and eax, 1
        ///
        /// Confirmed against four captures: 0 in both regular, 4 in both codcast.
        /// **Read only.** The batch checks the mode; it never sets it.
        /// </summary>
        public const int DemoModeOffset = 0x2CD3408;
        public const int CodcastBit = 0x04;

        public int DemoModeFlags
        {
            get
            {
                int v = ReadInt(new IntPtr(_base.ToInt64() + DemoModeOffset));
                return v == int.MinValue ? -1 : (v & 0xFF);
            }
        }

        /// <summary>True when the replay is running in CODCAST film mode.</summary>
        public bool IsCodcast { get { int f = DemoModeFlags; return f >= 0 && (f & CodcastBit) != 0; } }

        public const int RosterStride = 0x120;
        public const int RosterSlots = 18;

        /// <summary>The name in one spectate slot, or "" if that slot is empty.</summary>
        public string PlayerName(int slot)
        {
            if (slot < 0 || slot >= RosterSlots) return "";
            string s = ReadString(new IntPtr(_base.ToInt64() + RosterOffset + (slot * RosterStride)), 48);
            return s == null ? "" : s.Trim();
        }

        /// <summary>Every occupied slot, in spectate-index order.</summary>
        public string[] Roster()
        {
            var names = new string[RosterSlots];
            for (int i = 0; i < RosterSlots; i++) names[i] = PlayerName(i);
            return names;
        }

        /// <summary>
        /// The slot for a gamertag, or -1. Matched loosely because the take list is
        /// typed by hand and clan tags come and go: exact first, then ignoring case
        /// and spaces, then "contains".
        /// </summary>
        public int SlotForName(string wanted)
        {
            if (string.IsNullOrEmpty(wanted)) return -1;
            string[] names = Roster();

            for (int i = 0; i < names.Length; i++)
                if (string.Equals(names[i], wanted, StringComparison.OrdinalIgnoreCase)) return i;

            string flat = Flatten(wanted);
            for (int i = 0; i < names.Length; i++)
                if (names[i].Length > 0 && Flatten(names[i]) == flat) return i;

            for (int i = 0; i < names.Length; i++)
                if (names[i].Length > 0 && Flatten(names[i]).IndexOf(flat, StringComparison.Ordinal) >= 0) return i;

            return -1;
        }

        private static string Flatten(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>
        /// A bool dvar holds ONE byte, so reading four picks up whatever field sits
        /// beside it: a neighbour byte of 1 over a value of 0 reads as 256, which
        /// anything testing "!= 0" calls on.
        /// </summary>
        public int ReadDvarBool(string name, int fallback = int.MinValue)
        {
            int v = ReadDvarInt(name, int.MinValue);
            return v == int.MinValue ? fallback : (v & 0xFF);
        }

        /// <summary>
        /// The text at an address, up to the first zero byte. For string dvars the
        /// int at +24 is a pointer rather than a value, so this is how to see what
        /// one holds. Read-only, like everything in this class.
        /// </summary>
        public string ReadString(IntPtr address, int max = 160)
        {
            byte[] buf = new byte[max];
            IntPtr got;
            if (!ReadProcessMemory(_handle, address, buf, max, out got)) return null;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < got.ToInt32() && i < max; i++)
            {
                byte b = buf[i];
                if (b == 0) return sb.ToString();
                if (b < 0x20 || b > 0x7E) return null;   // not text, so do not pretend it is
                sb.Append((char)b);
            }
            return null;   // no terminator inside the window
        }

        /// <summary>
        /// What a dvar is and what it holds, for probing a set of names without
        /// assuming which are dvars and which are commands. Presence is the datum
        /// that matters: a name that is a COMMAND is not registered as a dvar and
        /// reads as absent here.
        /// </summary>
        public string DescribeDvar(string name)
        {
            IntPtr d = FindDvar(name);
            if (d == IntPtr.Zero) return "not a dvar";

            int v = ReadInt(new IntPtr(d.ToInt64() + 24));
            if (v == int.MinValue) return "dvar, value unreadable";

            string text = null;
            if (v > 0x00010000 && v < 0x7FFF0000) text = ReadString(new IntPtr(v));

            // Some of these really are floats: 0x453B8000 is 3000.0, not
            // 1,161,527,296. Only shown when the
            // bit pattern decodes to something a setting would plausibly be, so
            // string pointers and small ints do not sprout a nonsense column.
            float f = BitConverter.ToSingle(BitConverter.GetBytes(v), 0);
            bool looksFloat = v > 65535 && !float.IsNaN(f) && Math.Abs(f) >= 0.001f && Math.Abs(f) < 1e7f;

            return "int " + v + "  0x" + v.ToString("X8") + "  as bool " + (v & 0xFF)
                 + (looksFloat ? "  as float " + f.ToString("0.###") : "")
                 + (text != null ? "  text \"" + text + "\"" : "");
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
            if (_writeHandle != IntPtr.Zero) { CloseHandle(_writeHandle); _writeHandle = IntPtr.Zero; }
        }
    }
}
