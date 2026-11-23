using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyASR.Asr;
using MyASR.Asr.Whisper;
using MyASR.Audio;
using MyASR.Hotkeys;
using MyASR.Models;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.Net.Http;

namespace MyASR;

internal static class Program
{
    [STAThread]
    static void Main()
    {
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
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply(app, UserSettings.Load().DarkTheme);
        var splash = new SplashForm();
        using var startup = new CancellationTokenSource();
        splash.Closing += CancelStartup;
        splash.ContentRendered += StartMainWindow;
        app.Run(splash);

        void CancelStartup(object? sender, CancelEventArgs e)
        {
            e.Cancel = true;
            startup.Cancel();
            splash.ReportCancellation();
        }

        async void StartMainWindow(object? sender, EventArgs e)
        {
            splash.ContentRendered -= StartMainWindow;
            try
            {
                startup.Token.ThrowIfCancellationRequested();
                var main = sp.GetRequiredService<Main>();
                if (!await main.PrepareAsync(splash, startup.Token)) return;
                splash.ReportReady();
                await Dispatcher.Yield(DispatcherPriority.Background);
                startup.Token.ThrowIfCancellationRequested();
                app.MainWindow = main;
                main.Show();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            catch (OperationCanceledException) when (startup.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!startup.IsCancellationRequested)
                {
                    System.Diagnostics.Trace.TraceError("Ошибка запуска MyASR: {0}", ex);
                    new ErrorWindow("Не удалось подготовить диктовку", ex.Message) { Owner = splash }.ShowDialog();
                }
            }
            finally
            {
                splash.Closing -= CancelStartup;
                splash.Close();
                if (app.ShutdownMode == ShutdownMode.OnExplicitShutdown) app.Shutdown();
            }
        }
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
