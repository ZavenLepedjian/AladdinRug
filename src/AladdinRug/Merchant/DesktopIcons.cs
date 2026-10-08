using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AladdinRug
{
    /// <summary>One icon on the desktop: its name and where it sits (screen pixels, top-left of its cell).</summary>
    internal struct DesktopIcon
    {
        public int Index;
        public string Name;
        public int X, Y;
    }

    /// <summary>
    /// Reads and moves the icons on the Windows desktop (the desktop is a list view inside Explorer). Only icon POSITIONS are
    /// ever changed. Files and folders are never touched.
    /// </summary>
    internal sealed class DesktopIcons : IDisposable
    {
        private const int LVM_GETITEMCOUNT = 0x1004, LVM_SETITEMPOSITION = 0x100F, LVM_GETITEMPOSITION = 0x1010,
                          LVM_GETITEMSPACING = 0x1033, LVM_GETITEMTEXTW = 0x1073;
        private const int LVS_AUTOARRANGE = 0x0100;
        private const uint PROCESS_VM_OPERATION = 0x8, PROCESS_VM_READ = 0x10, PROCESS_VM_WRITE = 0x20, PROCESS_QUERY_INFORMATION = 0x400;
        private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x4;
        private const int GWL_STYLE = -16;

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Native.POINT pt);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr data);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);
        [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);
        [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);

        private delegate bool EnumProc(IntPtr hwnd, IntPtr data);

        private readonly IntPtr _list;
        private IntPtr _process, _remote;
        private const int RemoteSize = 1024;

        public bool AutoArranged { get; private set; }
        public int CellW { get; private set; }
        public int CellH { get; private set; }

        private DesktopIcons(IntPtr list) { _list = list; }

        /// <summary>Open the desktop's icon list, or null if it can't be reached (icons hidden, no permission...).</summary>
        public static DesktopIcons Open()
        {
            IntPtr list = FindList();
            if (list == IntPtr.Zero) return null;
            var icons = new DesktopIcons(list);
            if (!icons.Attach()) { icons.Dispose(); return null; }
            return icons;
        }

        private static IntPtr FindList()
        {
            IntPtr found = IntPtr.Zero;
            EnumProc search = null;
            search = (h, d) =>
            {
                if (Native.ClassOf(h) == "SysListView32" && IsWindowVisible(h)) { found = h; return false; }
                return true;
            };
            IntPtr shell = Native.GetShellWindow();
            if (shell != IntPtr.Zero) EnumChildWindows(shell, search, IntPtr.Zero);
            if (found == IntPtr.Zero)                          // with a slideshow wallpaper the icons live under a WorkerW window instead
                EnumWindows((h, d) =>
                {
                    string cls = Native.ClassOf(h);
                    if (cls == "WorkerW") EnumChildWindows(h, search, IntPtr.Zero);
                    return found == IntPtr.Zero;
                }, IntPtr.Zero);
            return found;
        }

        private bool Attach()
        {
            GetWindowThreadProcessId(_list, out uint pid);
            _process = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, pid);
            if (_process == IntPtr.Zero) return false;
            _remote = VirtualAllocEx(_process, IntPtr.Zero, (UIntPtr)RemoteSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (_remote == IntPtr.Zero) return false;

            AutoArranged = (GetWindowLongPtr(_list, GWL_STYLE).ToInt64() & LVS_AUTOARRANGE) != 0;
            long spacing = SendMessage(_list, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero).ToInt64();      // large-icon view
            CellW = (int)(spacing & 0xFFFF);
            CellH = (int)((spacing >> 16) & 0xFFFF);
            return true;
        }

        public int Count => (int)SendMessage(_list, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt64();

        private Native.POINT Origin()
        {
            var o = new Native.POINT();
            ClientToScreen(_list, ref o);
            return o;
        }

        public List<DesktopIcon> GetIcons()
        {
            var result = new List<DesktopIcon>();
            Native.POINT origin = Origin();
            int count = Count;
            for (int i = 0; i < count; i++) result.Add(ReadItem(i, origin));
            return result;
        }

        /// <summary>Find one icon by name, looking from the end of the list (where new ones are added). Null if it isn't there.</summary>
        public DesktopIcon? Find(string name)
        {
            Native.POINT origin = Origin();
            for (int i = Count - 1; i >= 0; i--)
            {
                DesktopIcon icon = ReadItem(i, origin);
                if (string.Equals(icon.Name, name, StringComparison.OrdinalIgnoreCase)) return icon;
            }
            return null;
        }

        private DesktopIcon ReadItem(int i, Native.POINT origin)
        {
            var buf = new byte[8];
            // position: Explorer writes a POINT into our block of its memory
            SendMessage(_list, LVM_GETITEMPOSITION, (IntPtr)i, _remote);
            ReadProcessMemory(_process, _remote, buf, (UIntPtr)8, out _);
            int x = BitConverter.ToInt32(buf, 0), y = BitConverter.ToInt32(buf, 4);

            // name: an LVITEMW pointing at a text buffer further along the same block
            IntPtr text = _remote + 256;
            var item = new byte[Marshal.SizeOf<LVITEM>()];
            var lv = new LVITEM { iSubItem = 0, pszText = text, cchTextMax = 260 };
            IntPtr pLv = Marshal.AllocHGlobal(item.Length);
            try
            {
                Marshal.StructureToPtr(lv, pLv, false);
                Marshal.Copy(pLv, item, 0, item.Length);
            }
            finally { Marshal.FreeHGlobal(pLv); }
            WriteProcessMemory(_process, _remote + 64, item, (UIntPtr)item.Length, out _);
            int len = (int)SendMessage(_list, LVM_GETITEMTEXTW, (IntPtr)i, _remote + 64).ToInt64();
            var textBytes = new byte[Math.Max(2, Math.Min(len, 259) * 2)];
            ReadProcessMemory(_process, text, textBytes, (UIntPtr)textBytes.Length, out _);
            return new DesktopIcon { Index = i, Name = Encoding.Unicode.GetString(textBytes, 0, Math.Min(len, 259) * 2), X = origin.X + x, Y = origin.Y + y };
        }

        /// <summary>Put the icon at <paramref name="index"/> with its cell's top-left at this screen position.</summary>
        public void Move(int index, int screenX, int screenY)
        {
            Native.POINT origin = Origin();
            int x = screenX - origin.X, y = screenY - origin.Y;
            SendMessage(_list, LVM_SETITEMPOSITION, (IntPtr)index, (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF)));
        }

        /// <summary>
        /// Put icons at exact places (screen pixels, an icon's cell top-left), by name. The desktop allows one icon per cell, so an icon
        /// whose place is taken waits until it is free; when icons are sitting in each other's places (a swap) one is parked in a
        /// free cell for a moment. Returns how many ended up where asked.
        /// </summary>
        public int Arrange(IEnumerable<(string name, int x, int y)> targets)
        {
            var pending = new List<(string name, int x, int y)>(targets);
            int done = 0;
            for (int round = 0; round < 12 && pending.Count > 0; round++)
            {
                List<DesktopIcon> now = GetIcons();
                var byName = new Dictionary<string, DesktopIcon>();
                foreach (DesktopIcon i in now) if (!byName.ContainsKey(i.Name)) byName[i.Name] = i;
                var occupied = new HashSet<(int, int)>();
                foreach (DesktopIcon i in now) occupied.Add((i.X, i.Y));

                bool progress = false;
                foreach (var t in pending.ToArray())
                {
                    if (!byName.TryGetValue(t.name, out DesktopIcon icon)) { pending.Remove(t); continue; }
                    if (icon.X == t.x && icon.Y == t.y) { pending.Remove(t); done++; continue; }
                    if (occupied.Contains((t.x, t.y))) continue;            // someone is in that place: wait for it to leave
                    Move(icon.Index, t.x, t.y);
                    occupied.Remove((icon.X, icon.Y));
                    occupied.Add((t.x, t.y));
                    pending.Remove(t);
                    done++;
                    progress = true;
                }

                if (!progress && pending.Count > 0)
                {
                    // Icons are in each other's places. Park one of the blockers in a free cell; it has its own target to go to later.
                    var first = pending[0];
                    DesktopIcon blocker = now.Find(i => i.X == first.x && i.Y == first.y);
                    if (blocker.Name == null || !pending.Exists(p => p.name == blocker.Name)) break;     // never shove an icon that isn't part of this
                    (int fx, int fy) = FreeCell(now, occupied, first.x, first.y);
                    Move(blocker.Index, fx, fy);
                }
            }
            return done;
        }

        // The nearest empty cell (on the grid the icons sit on) to this place.
        private (int, int) FreeCell(List<DesktopIcon> icons, HashSet<(int, int)> occupied, int nearX, int nearY)
        {
            int cw = Math.Max(1, CellW), ch = Math.Max(1, CellH);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (DesktopIcon i in icons) { minX = Math.Min(minX, i.X); minY = Math.Min(minY, i.Y); maxX = Math.Max(maxX, i.X); maxY = Math.Max(maxY, i.Y); }   // stay where icons already are: on screen
            for (int ring = 1; ring < 60; ring++)
                for (int dy = -ring; dy <= ring; dy++)
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;
                        int x = nearX + dx * cw, y = nearY + dy * ch;
                        if (x < minX || x > maxX || y < minY || y > maxY || occupied.Contains((x, y))) continue;
                        return (x, y);
                    }
            return (nearX, nearY);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEM
        {
            public uint mask; public int iItem, iSubItem; public uint state, stateMask;
            public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam;
        }

        public void Dispose()
        {
            if (_remote != IntPtr.Zero) VirtualFreeEx(_process, _remote, UIntPtr.Zero, MEM_RELEASE);
            if (_process != IntPtr.Zero) CloseHandle(_process);
            _remote = _process = IntPtr.Zero;
        }
    }
}
