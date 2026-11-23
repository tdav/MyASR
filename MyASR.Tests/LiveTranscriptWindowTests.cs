using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyASR.Asr;
using MyASR.Models;
using TUnit.Core.Executors;

namespace MyASR.Tests;

public class LiveTranscriptWindowTests
{
    private sealed class SpeechEvents : IAsrService
    {
        public AsrState State { get; private set; }
        public ModelDescriptor CurrentModel => ModelCatalog.Default;
        public event Action<AsrStateChanged>? StateChanged;
        public event Action<string>? PreviewReady;
        public event Action<AsrResult>? ResultReady;
        public event Action<Exception>? Failed { add { } remove { } }

        public void ChangeState(AsrState state)
        {
            State = state;
            StateChanged?.Invoke(new AsrStateChanged(state));
        }
        public void Preview(string text) => PreviewReady?.Invoke(text);
        public void Complete(string text)
        {
            ChangeState(AsrState.Decoding);
            ResultReady?.Invoke(new AsrResult(text, TimeSpan.Zero, TimeSpan.Zero));
            ChangeState(AsrState.Idle);
        }

        public void StartCapture(string deviceId) => ChangeState(AsrState.Recording);
        public Task StopAndRecognizeAsync() => throw new NotSupportedException();
        public Task WarmupAsync() => throw new NotSupportedException();
        public Task SwitchModelAsync(ModelDescriptor model, CancellationToken ct) => throw new NotSupportedException();
    }

    [Test, STAThreadExecutor]
    public async Task PreviewRevisionsAndFinals_UpdateTheSameField_AndCopyOnlyOnCommit()
    {
        var speech = new SpeechEvents();
        var clipboard = new List<string>();
        var window = new Main(speech, null!, null!, null!, null!, null!, writeClipboard: clipboard.Add);
        var box = (TextBox)window.FindName("ResultBox");
        var clear = (Button)window.FindName("ClearButton");
        var copy = (Button)window.FindName("CopyButton");
        var views = new List<string>();
        void Drain() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        speech.StartCapture("test");
        Task.Run(() => speech.Preview("Первая гипотеза")).GetAwaiter().GetResult();
        Drain();
        views.Add(box.Text);
        var copiesDuringRecording = clipboard.Count;
        speech.Complete("Первая фраза.");
        speech.Preview("Запоздавший черновик");
        speech.StartCapture("test");
        speech.Preview("Вторая");
        speech.Preview("Ветка");
        Drain();
        views.Add(box.Text);
        var labelWithDraft = clear.Content?.ToString();
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        views.Add(box.Text);
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        speech.Complete("Ветка дерева.");
        Drain();
        views.Add(box.Text);

        speech.StartCapture("test");
        speech.Preview("Лишнее");
        speech.Complete("");
        speech.StartCapture("test");
        speech.Preview("Незавершённое");
        speech.ChangeState(AsrState.Idle);
        Drain();
        views.Add(box.Text);
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        views.Add(box.Text);

        var nl = Environment.NewLine;
        await Assert.That(views).IsEquivalentTo([
            "Первая гипотеза", $"Первая фраза.{nl}Ветка", "Ветка",
            $"Ветка дерева.{nl}", $"Ветка дерева.{nl}", ""
        ], CollectionOrdering.Matching);
        await Assert.That(labelWithDraft).IsEqualTo("Очистить историю");
        await Assert.That(copiesDuringRecording).IsEqualTo(0);
        await Assert.That(clipboard).IsEquivalentTo(["Первая фраза.", "Ветка", "Ветка дерева."], CollectionOrdering.Matching);
    }
}
