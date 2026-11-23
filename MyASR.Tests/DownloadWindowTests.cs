using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyASR.Models;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace MyASR.Tests;

public class DownloadWindowTests
{
    private sealed class Provider(Func<IProgress<DownloadProgress>?, CancellationToken, Task> acquire) : IModelProvider
    {
        public bool AreModelsAvailable(ModelPaths paths) => false;
        public Task EnsureDownloadedAsync(ModelPaths paths, IProgress<DownloadProgress>? progress, CancellationToken ct)
            => acquire(progress, ct);
    }

    private static Task<T> OnSta<T>(Func<T> run)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(run()); }
            catch (Exception ex) { result.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Test]
    public async Task RetryAfterFailure_ShowsDetailAndReturnsSuccess()
    {
        var result = await OnSta(() =>
        {
            DownloadForm dialog = null!;
            int attempts = 0;
            bool retryOffered = false;
            var provider = new Provider((_, _) =>
            {
                if (++attempts > 1) return Task.CompletedTask;
                dialog.Dispatcher.BeginInvoke(() =>
                {
                    var retry = (Button)dialog.FindName("RetryButton");
                    retryOffered = retry.IsVisible &&
                        ((TextBlock)dialog.FindName("DetailText")).Text.Contains("I/O test");
                    retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                return Task.FromException(new IOException("I/O test"));
            });
            dialog = new DownloadForm(provider, new ModelPaths(".", ModelCatalog.Default));
            return (success: dialog.ShowDialog(), retryOffered, attempts);
        });
        await Assert.That(result.success).IsTrue();
        await Assert.That(result.retryOffered).IsTrue();
        await Assert.That(result.attempts).IsEqualTo(2);
    }

    [Test]
    public async Task ClosingDuringTransfer_ReportsProgressAndWaitsForCancellation()
    {
        var result = await OnSta(() =>
        {
            DownloadForm dialog = null!;
            double percent = -1;
            bool cleanedUp = false;
            var provider = new Provider(async (progress, token) =>
            {
                progress!.Report(new DownloadProgress("encoder", 50, 100, 2000, 0, 1));
                _ = dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    percent = ((ProgressBar)dialog.FindName("DownloadBar")).Value;
                    dialog.Close();
                });
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cleanedUp = token.IsCancellationRequested; }
            });
            dialog = new DownloadForm(provider, new ModelPaths(".", ModelCatalog.Default));
            return (success: dialog.ShowDialog(), percent, cleanedUp);
        });
        await Assert.That(result.success).IsFalse();
        await Assert.That(result.percent).IsEqualTo(50);
        await Assert.That(result.cleanedUp).IsTrue();
    }
}
