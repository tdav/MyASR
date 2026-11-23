using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.Logging;
using MyASR.Models;

namespace MyASR;

public partial class DownloadForm : Window
{
    private readonly IModelProvider provider;
    private readonly ModelPaths paths;
    private readonly ILogger<DownloadForm>? logger;
    private CancellationTokenSource? active;
    private bool closed;

    public DownloadForm(IModelProvider provider, ModelPaths paths, ILogger<DownloadForm>? logger = null)
    {
        InitializeComponent();
        this.provider = provider;
        this.paths = paths;
        this.logger = logger;
        ModelName.Text = paths.Model.DisplayName;
        LicenceText.Text = paths.Model.LicenceNote;
        Title = $"Загрузка модели — {paths.Model.DisplayName}";
        ContentRendered += FirstRender;
    }

    private async void FirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= FirstRender;
        await DownloadAsync();
    }

    private async void RetryClicked(object sender, RoutedEventArgs e) => await DownloadAsync();

    private async Task DownloadAsync()
    {
        if (active != null || closed) return;
        using var attempt = new CancellationTokenSource();
        active = attempt;
        RetryButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "Подготовка файлов…";
        DetailText.Text = "";
        DownloadBar.Value = 0;
        DownloadBar.IsIndeterminate = true;
        PercentageText.Text = "";
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (active != attempt || attempt.IsCancellationRequested || closed) return;
            DownloadBar.IsIndeterminate = p.Total <= 0;
            DownloadBar.Value = DownloadProgressFormatter.Percent(p);
            PercentageText.Text = p.Total > 0 ? $"{DownloadBar.Value:0} %" : "";
            StatusText.Text = DownloadProgressFormatter.Line(paths.Model, p);
            DetailText.Text = DownloadProgressFormatter.Speed(p);
        });
        try
        {
            await provider.EnsureDownloadedAsync(paths, progress, attempt.Token);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
        catch (Exception ex) when (!attempt.IsCancellationRequested)
        {
            logger?.LogError(ex, "Model download failed for {ModelId}", paths.Model.Id);
            if (!closed)
            {
                DownloadBar.IsIndeterminate = false;
                StatusText.Text = "Не удалось загрузить модель";
                DetailText.Text = DownloadProgressFormatter.FailureDetail(paths.Model, ex);
                RetryButton.Visibility = Visibility.Visible;
                RetryButton.Focus();
            }
            return;
        }
        catch (Exception ex) when (attempt.IsCancellationRequested)
        {
            logger?.LogDebug(ex, "Model acquisition ended during cancellation");
        }
        finally
        {
            active = null;
        }
        // A provider can finish inline inside Cancel(). Let the first Closing event unwind.
        await Task.Yield();
        if (!closed) DialogResult = !attempt.IsCancellationRequested;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (active == null) return;
        // Release partial-file handles before the owner can start another attempt.
        e.Cancel = true;
        StatusText.Text = "Отмена загрузки…";
        CancelButton.IsEnabled = false;
        active.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        base.OnClosed(e);
    }
}
