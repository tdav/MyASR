using System.Windows;
using System.Windows.Threading;

namespace MyASR;

public partial class SplashForm : Window
{
    public SplashForm()
    {
        InitializeComponent();
        StageLabel.Text = "Запуск приложения…";
    }

    public async Task ShowStageAsync(int stage, string status, bool indeterminate = false)
    {
        StageLabel.Text = status;
        StageCount.Text = $"Этап {stage} из {StageProgress.Maximum:0}";
        StageProgress.Value = stage - 1;
        StageProgress.IsIndeterminate = indeterminate;
        // Paint each milestone before starting its work, including stages served from the OS cache.
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    public void ReportReady()
    {
        StageLabel.Text = "Подготовка завершена";
        StageCount.Text = "Готово";
        StageProgress.IsIndeterminate = false;
        StageProgress.Value = StageProgress.Maximum;
    }

    internal void ReportCancellation()
    {
        StageLabel.Text = "Завершение запуска…";
        StageCount.Text = "";
        StageProgress.IsIndeterminate = true;
    }
}
