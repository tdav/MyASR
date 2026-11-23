using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MyASR.Asr;
using MyASR.Models;
using MyASR.Tests.Asr;

namespace MyASR.Tests;

public class RecordingToggleTests
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    // Stage the ready state without starting model migration, autostart or user settings writes.
    private static Main CreateReadyWindow(AsrService service, FakeMic mic)
    {
        var window = new Main(service, mic, null!, null!, null!, null!);
        var devices = (ComboBox)window.FindName("DeviceCombo");
        devices.ItemsSource = mic.EnumerateDevices();
        devices.SelectedIndex = 0;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Main).GetField("modelReady", flags)!.SetValue(window, true);
        typeof(Main).GetField("preparing", flags)!.SetValue(window, false);
        ((Button)window.FindName("RecordButton")).IsEnabled = true;
        return window;
    }

    [Test]
    public async Task RecordButtonAndGlobalHotkey_ToggleOnlyOnActivation()
    {
        var result = new TaskCompletionSource<AsrState[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var mic = new FakeMic();
                using var service = new AsrService(mic, _ => new FakeEngine(), ModelCatalog.Default);
                var states = new List<AsrState>();
                service.StateChanged += change => states.Add(change.State);
                var window = CreateReadyWindow(service, mic);
                using var messages = new HwndSource(new HwndSourceParameters("MyASR test") { WindowStyle = 0 });
                var hook = typeof(Main).GetMethod("WindowMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
                messages.AddHook(hook.CreateDelegate<HwndSourceHook>(window));
                var record = (Button)window.FindName("RecordButton");
                void Click() => record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Hotkey() => SendMessage(messages.Handle, 0x0312, 0xA001, 0);

                Click();
                record.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                record.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, record, null)
                    { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                if (!mic.Started || service.State != AsrState.Recording)
                    throw new InvalidOperationException("The first click must keep recording after release and focus loss.");
                Click();
                Hotkey();
                Click();
                Click();
                Hotkey();
                result.SetResult(states.ToArray());
            }
            catch (Exception ex) { result.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var actual = await result.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(actual).IsEquivalentTo([
            AsrState.Recording, AsrState.Idle,
            AsrState.Recording, AsrState.Idle,
            AsrState.Recording, AsrState.Idle
        ], CollectionOrdering.Matching);
    }
}
