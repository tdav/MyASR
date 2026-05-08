using Microsoft.Extensions.Logging;
using MyASR.Models;

namespace MyASR;

public partial class DownloadForm : Form
{
    private readonly IModelProvider _provider;
    private readonly ModelPaths _paths;
    private readonly ILogger<DownloadForm>? _logger;
    private CancellationTokenSource? _cts;

    public DownloadForm(IModelProvider provider, ModelPaths paths, ILogger<DownloadForm>? logger = null)
    {
        InitializeComponent();
        _provider = provider; _paths = paths; _logger = logger;
        _retry.Click += async (_, _) => await StartDownloadAsync();
        Load += async (_, _) => await StartDownloadAsync();
    }

    public bool DownloadSucceeded { get; private set; }

    private async Task StartDownloadAsync()
    {
        _retry.Visible = false;
        _progress.Value = 0;
        _label.Text = "Скачивание моделей...";
        _detail.Text = "";

        _cts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(OnProgress);
        try
        {
            await _provider.EnsureDownloadedAsync(_paths, progress, _cts.Token);
            DownloadSucceeded = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Model download failed");
            _label.Text = "Ошибка загрузки. Проверьте интернет и нажмите «Повторить».";
            _detail.Text = ex.Message;
            _retry.Visible = true;
        }
    }

    private void OnProgress(DownloadProgress p)
    {
        if (InvokeRequired) { BeginInvoke(() => OnProgress(p)); return; }
        _label.Text = $"{p.FileName} — {p.Downloaded / 1_000_000} / {p.Total / 1_000_000} MB";
        _progress.Value = (int)Math.Clamp(p.Downloaded * 100.0 / Math.Max(1, p.Total), 0, 100);
        var mbPerSec = p.BytesPerSecond / 1_000_000.0;
        _detail.Text = mbPerSec > 0 ? $"{mbPerSec:F1} MB/s" : "";
    }
}
