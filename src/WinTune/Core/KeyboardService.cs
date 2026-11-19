using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinTune.Core;

/// <summary>
/// One shortcut: while the trigger key is held, pressing <see cref="Key"/> types <see cref="Normal"/>
/// (or <see cref="Shift"/> with Shift held).
/// </summary>
public sealed class KeyMapping
{
    public string Key { get; set; } = "";

    public string Normal { get; set; } = "";

    public string Shift { get; set; } = "";

    public static List<KeyMapping> Defaults() => new()
    {
        new KeyMapping { Key = "a", Normal = "à", Shift = "À" },
        new KeyMapping { Key = "e", Normal = "è", Shift = "È" },
        new KeyMapping { Key = "i", Normal = "ì", Shift = "Ì" },
        new KeyMapping { Key = "o", Normal = "ò", Shift = "Ò" },
        new KeyMapping { Key = "u", Normal = "ù", Shift = "Ù" },
        new KeyMapping { Key = "'", Normal = "é", Shift = "É" },
        new KeyMapping { Key = "4", Normal = "€", Shift = "€" },
    };
}

/// <summary>
/// Types accented letters with a key combination, on top of any keyboard layout (for example a US keyboard).
/// A keyboard hook on its own thread watches the keys: while the trigger key (Right Alt by default) is held,
/// the keys of the table are replaced by the matching characters, sent as Unicode text.
/// The trigger key is used only for this while the feature is on.
/// </summary>
public static class KeyboardService
{
    public const string RightAlt = "RightAlt";
    public const string RightCtrl = "RightCtrl";

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x10;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyUp = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;

    private static readonly object Gate = new();
    private static readonly HookProc Proc = HookCallback;
    private static readonly HashSet<int> Swallowed = new();

    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _hook;

    private static volatile int _triggerVk = 0xA5;
    private static volatile bool _triggerDown;
    private static Dictionary<int, (string Normal, string Shift)> _map = new();

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>Applies the settings: starts or stops the hook and loads the table.</summary>
    public static void Apply()
    {
        AppSettings settings = AppSettings.Current;

        if (!settings.KeyboardEnabled)
        {
            Stop();
            return;
        }

        _triggerVk = settings.KeyboardTrigger == RightCtrl ? 0xA3 : 0xA5;
        _triggerDown = false;
        Swallowed.Clear();

        var map = new Dictionary<int, (string, string)>();
        foreach (KeyMapping m in settings.KeyboardMappings)
        {
            int vk = ResolveVk(m.Key);
            if (vk > 0 && m.Normal.Length > 0)
            {
                map[vk] = (m.Normal, m.Shift.Length > 0 ? m.Shift : m.Normal.ToUpperInvariant());
            }
        }

        _map = map;
        Start();
    }

    /// <summary>Virtual key of the character on the current layout; 0 if it has none.</summary>
    public static int ResolveVk(string key)
    {
        if (key.Length != 1)
        {
            return 0;
        }

        short result = VkKeyScan(key[0]);
        return result == -1 ? 0 : result & 0xFF;
    }

    private static void Start()
    {
        lock (Gate)
        {
            if (_thread is not null)
            {
                return;
            }

            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "WinTune keyboard" };
            _thread.Start();
            ready.Wait(3000);
        }
    }

    private static void Stop()
    {
        lock (Gate)
        {
            if (_thread is null)
            {
                return;
            }

            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
            _thread = null;
            _triggerDown = false;
            Swallowed.Clear();
        }
    }

    private static void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WhKeyboardLl, Proc, GetModuleHandle(null), 0);
        ready.Set();

        if (_hook == IntPtr.Zero)
        {
            return;
        }

        while (GetMessage(out Msg msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if ((k.Flags & LlkhfInjected) == 0 && Handle((int)wParam, (int)k.VkCode, (int)k.ScanCode))
                {
                    return (IntPtr)1;
                }
            }
        }
        catch
        {
            // A keyboard hook must never throw: the key goes through untouched.
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>True if the key event is consumed.</summary>
    private static bool Handle(int message, int vk, int scanCode)
    {
        bool down = message is WmKeyDown or WmSysKeyDown;
        bool up = message is WmKeyUp or WmSysKeyUp;

        // On layouts with AltGr (US-International, Italian...) Windows adds a fake Left Ctrl (scan code 0x21D)
        // to every Right Alt: it is dropped, otherwise the programs would see Ctrl while the text is typed.
        if (vk == 0xA2 && scanCode == 0x21D && _triggerVk == 0xA5)
        {
            return true;
        }

        if (vk == _triggerVk)
        {
            _triggerDown = down;
            return true;
        }

        if (up)
        {
            return Swallowed.Remove(vk);
        }

        if (!down || !_triggerDown || !_map.TryGetValue(vk, out (string Normal, string Shift) text))
        {
            return false;
        }

        // Other shortcuts (Ctrl, Win, left Alt) are left alone.
        if ((_triggerVk != 0xA5 && IsDown(0xA2)) || IsDown(0x5B) || IsDown(0x5C) || IsDown(0xA4))
        {
            return false;
        }

        SendText(IsDown(0x10) ? text.Shift : text.Normal);
        Swallowed.Add(vk);
        return true;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void SendText(string text)
    {
        var inputs = new List<Input>();
        foreach (char c in text)
        {
            inputs.Add(Unicode(c, false));
            inputs.Add(Unicode(c, true));
        }

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
    }

    private static Input Unicode(char c, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Ki = new KeybdInput
            {
                Vk = 0,
                Scan = c,
                Flags = KeyeventfUnicode | (keyUp ? KeyeventfKeyUp : 0),
            },
        },
    };

    // ---------- Native ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mi;

        [FieldOffset(0)]
        public KeybdInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
