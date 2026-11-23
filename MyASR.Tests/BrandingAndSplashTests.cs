using System.Windows.Media.Imaging;
using TUnit.Core.Executors;

namespace MyASR.Tests;

public class BrandingAndSplashTests
{
    [Test, STAThreadExecutor]
    public async Task ShippedIcon_ContainsSmallAndHighResolutionFrames()
    {
        using var stream = System.Windows.Application.GetResourceStream(
            new Uri("/MyASR;component/icon.ico", UriKind.Relative)).Stream;
        var icon = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        await Assert.That(icon.Frames.Select(frame => frame.PixelWidth).ToArray())
            .IsEquivalentTo([16, 24, 32, 48, 64, 128, 256]);
    }

    [Test, STAThreadExecutor]
    public async Task Splash_ShowsCompletedStagesAndIndeterminateModelLoading()
    {
        var splash = new SplashForm();
        try
        {
            var bar = (System.Windows.Controls.ProgressBar)splash.FindName("StageProgress");
            var counter = (System.Windows.Controls.TextBlock)splash.FindName("StageCount");
            var label = (System.Windows.Controls.TextBlock)splash.FindName("StageLabel");
            var stage = splash.ShowStageAsync(3, "Загрузка модели", indeterminate: true);
            splash.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await stage;
            var during = (bar.IsIndeterminate, bar.Value, counter.Text, status: label.Text);
            var finalStage = splash.ShowStageAsync(4, "Настройка клавиш");
            splash.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await finalStage;
            var beforeCompletion = (bar.IsIndeterminate, bar.Value);
            splash.ReportReady();
            var completed = (bar.IsIndeterminate, bar.Value, counter.Text);

            await Assert.That(during).IsEqualTo((true, 2d, "Этап 3 из 4", "Загрузка модели"));
            await Assert.That(beforeCompletion).IsEqualTo((false, 3d));
            await Assert.That(completed).IsEqualTo((false, 4d, "Готово"));
        }
        finally
        {
            splash.Close();
        }
    }
}
