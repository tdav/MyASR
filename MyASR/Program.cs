using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyASR.Asr;
using MyASR.Asr.Whisper;
using MyASR.Audio;
using MyASR.Hotkeys;
using MyASR.Models;
using System.IO;
using System.Net.Http;

namespace MyASR;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddConsole().AddDebug().SetMinimumLevel(LogLevel.Information);
            AddFileLogging(b);
        });

        services.AddSingleton<HttpClient>();
        services.AddSingleton<IModelSourceResolver>(_ =>
            new ModelSourceResolver(UserSettings.Load().ModelSources));
        services.AddSingleton<IModelProvider, ModelProvider>();
        services.AddSingleton<Func<ModelDescriptor, ModelPaths>>(_ =>
        {
            var root = ModelRoot.Resolve();
            return descriptor => new ModelPaths(root, descriptor);
        });
        services.AddSingleton<IMicCapture, WasapiMicCapture>();
        services.AddSingleton<IHotkeyManager, WindowsHotkeyManager>();

        // Engines are built lazily because they require model files on disk.
        services.AddSingleton<Func<ModelDescriptor, IAsrEngine>>(sp =>
        {
            var pathsFor = sp.GetRequiredService<Func<ModelDescriptor, ModelPaths>>();
            return descriptor => descriptor.Kind switch
            {
                AsrEngineKind.RnnT => GigaAmRnnTEngine.Create(pathsFor(descriptor)),
                AsrEngineKind.Whisper => WhisperOnnxEngine.Create(pathsFor(descriptor)),
                _ => throw new NotSupportedException($"Движок {descriptor.Kind} ещё не реализован.")
            };
        });

        services.AddSingleton<IAsrService>(sp => new AsrService(
            sp.GetRequiredService<IMicCapture>(),
            sp.GetRequiredService<Func<ModelDescriptor, IAsrEngine>>(),
            ModelCatalog.Default));

        services.AddSingleton<Func<ModelDescriptor, DownloadForm>>(sp => descriptor => new DownloadForm(
            sp.GetRequiredService<IModelProvider>(),
            sp.GetRequiredService<Func<ModelDescriptor, ModelPaths>>()(descriptor),
            sp.GetService<ILogger<DownloadForm>>()));
        services.AddTransient<Main>();

        using var sp = services.BuildServiceProvider();
        var form = sp.GetRequiredService<Main>();
        Application.Run(form);
    }

    private static void AddFileLogging(ILoggingBuilder b)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyASR", "logs");
        Directory.CreateDirectory(logDir);
        var path = Path.Combine(logDir, $"app-{DateTime.Now:yyyyMMdd}.log");
        var writer = new StreamWriter(path, append: true) { AutoFlush = true };
        b.AddProvider(new SimpleFileLoggerProvider(writer));
    }

    private sealed class SimpleFileLoggerProvider(TextWriter w) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new SimpleFileLogger(w, categoryName);
        public void Dispose() => w.Dispose();
    }

    private sealed class SimpleFileLogger(TextWriter w, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            lock (w) w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} {category} :: {formatter(state, ex)} {ex}");
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
