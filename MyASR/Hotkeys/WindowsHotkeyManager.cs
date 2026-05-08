using System.Runtime.InteropServices;

namespace MyASR.Hotkeys;

public sealed partial class WindowsHotkeyManager : IHotkeyManager
{
    private record Registered(IntPtr Hwnd, int Id);
    private readonly List<Registered> _registered = [];

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    public bool Register(IntPtr hWnd, int id, uint modifiers, uint vk)
    {
        if (RegisterHotKey(hWnd, id, modifiers, vk))
        {
            _registered.Add(new Registered(hWnd, id));
            return true;
        }
        return false;
    }

    public bool IsKeyDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

    public void Dispose()
    {
        foreach (var r in _registered) UnregisterHotKey(r.Hwnd, r.Id);
        _registered.Clear();
    }
}
