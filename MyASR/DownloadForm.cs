using Microsoft.Extensions.Logging;
using MyASR.Models;

namespace MyASR;

public partial class DownloadForm : Form
{
    private readonly IModelProvider provider;
    private readonly ModelPaths paths;
    private readonly ILogger<DownloadForm>? logger;
    private CancellationTokenSource? cts;

    // Дизайнер WinForms требует parameterless-конструктор для рендера формы.
    public DownloadForm()
    {
        InitializeComponent();
        provider = null!; paths = null!;
    }

    public DownloadForm(IModelProvider provider, ModelPaths paths, ILogger<DownloadForm>? logger = null)
        : this()
    {
        this.provider = provider; this.paths = paths; this.logger = logger;
        Text = $"Загрузка модели — {paths.Model.DisplayName}";
        _retry.Click += async (_, _) => await StartDownloadAsync();
        Load += async (_, _) => await StartDownloadAsync();
        FormClosing += (_, _) => cts?.Cancel();
    }

    public bool DownloadSucceeded { get; private set; }

    private async Task StartDownloadAsync()
    {
        _retry.Visible = false;
        _progress.Value = 0;
        _label.Text = $"Скачивание модели «{paths.Model.DisplayName}»...";
        _detail.Text = paths.Model.LicenceNote ?? "";

        cts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(OnProgress);
        try
        {
            await provider.EnsureDownloadedAsync(paths, progress, cts.Token);
            DownloadSucceeded = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            DialogResult = DialogResult.Cancel;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Model download failed for {ModelId}", paths.Model.Id);
            _label.Text = "Ошибка загрузки. Проверьте источник и нажмите «Повторить».";
            _detail.Text = ex.Message;
            _retry.Visible = true;
        }
    }

    private void OnProgress(DownloadProgress p)
    {
        if (InvokeRequired) { BeginInvoke(() => OnProgress(p)); return; }
        _label.Text = DownloadProgressFormatter.Line(paths.Model, p);
        _progress.Value = DownloadProgressFormatter.Percent(p);
        _detail.Text = DownloadProgressFormatter.Speed(p);
    }
}
