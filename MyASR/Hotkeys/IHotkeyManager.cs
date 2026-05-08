namespace MyASR.Hotkeys;

public interface IHotkeyManager : IDisposable
{
    bool Register(IntPtr windowHandle, int hotkeyId, uint modifiers, uint vkCode);
    bool IsKeyDown(uint vkCode);
}
