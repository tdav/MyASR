using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyASR.Asr;
using MyASR.Audio;
using MyASR.Features;
using MyASR.Hotkeys;
using MyASR.Models;
using System.Net.Http;

namespace MyASR;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().AddDebug().SetMinimumLevel(LogLevel.Information));

        services.AddSingleton<HttpClient>();
        services.AddSingleton<IReadOnlyList<ModelFileEntry>>(_ => ModelManifest.Files);
        services.AddSingleton<ModelPaths>(_ => ModelPaths.Default());
        services.AddSingleton<IModelProvider, HuggingFaceModelProvider>();
        services.AddSingleton<IMicCapture, WasapiMicCapture>();
        services.AddSingleton<IFeatureExtractor, MelSpectrogramExtractor>();
        services.AddSingleton<IHotkeyManager, WindowsHotkeyManager>();

        // Decoder is built lazily because it requires model files on disk.
        services.AddSingleton<Func<IRnnTDecoder>>(sp => () =>
        {
            var paths = sp.GetRequiredService<ModelPaths>();
            var vocab = Vocabulary.Load(paths.Vocab);
            return new RnnTGreedyDecoder(paths, vocab);
        });

        services.AddSingleton<IAsrService>(sp =>
        {
            var mic   = sp.GetRequiredService<IMicCapture>();
            var feats = sp.GetRequiredService<IFeatureExtractor>();
            var dec   = sp.GetRequiredService<Func<IRnnTDecoder>>()();
            return new AsrService(mic, feats, dec);
        });

        services.AddTransient<DownloadForm>();
        services.AddTransient<Form1>();

        using var sp = services.BuildServiceProvider();
        var form = sp.GetRequiredService<Form1>();
        Application.Run(form);
    }
}
