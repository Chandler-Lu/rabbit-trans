using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using RabTrans.Core.Hotkey;
using RabTrans.Core.OCR;
using RabTrans.Core.Screenshot;
using RabTrans.Core.Storage;
using RabTrans.Core.Sync;
using RabTrans.Core.Translation;
using RabTrans.Core.Clipboard;
using Serilog;

namespace RabTrans;

/// <summary>
/// Application entry point.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\RabTrans.SingleInstance";
    private const string SingleInstanceActivateEventName = @"Local\RabTrans.Activate";

    private static IServiceProvider? _serviceProvider;
    public static IServiceProvider Services => _serviceProvider ?? throw new InvalidOperationException("Services not initialized");

    private MainWindow? _mainWindow;
    private TaskbarIcon? _trayIcon;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activateListenerCancellation;

    public App()
    {
        // Configure logging
        var logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RabTrans", "logs", "rabtrans-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("RabTrans starting...");

        // Configure services
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // Handle unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
            Log.CloseAndFlush();
        };

        DispatcherUnhandledException += (s, e) =>
        {
            Log.Error(e.Exception, "Dispatcher unhandled exception");
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Core services
        services.AddSingleton<StorageService>();
        services.AddSingleton<CredentialService>();
        services.AddSingleton<ConfigSyncService>();
        
        // Feature services
        services.AddSingleton<TranslationService>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<ScreenshotService>();
    }

    protected override void OnStartup(StartupEventArgs args)
    {
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                SignalExistingInstance();
                Shutdown();
                return;
            }

            _ownsSingleInstanceMutex = true;
            StartActivationListener();
            base.OnStartup(args);

            var silentStartup = args.Args.Any(arg =>
                arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/silent", StringComparison.OrdinalIgnoreCase));

            // Create main window
            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;
            _mainWindow.InitializeServices();

            if (silentStartup)
            {
                new WindowInteropHelper(_mainWindow).EnsureHandle();
                Log.Information("RabTrans initialized in silent startup mode");
            }
            else
            {
                _mainWindow.Show();
                _mainWindow.Activate();
            }

            // Setup system tray
            SetupSystemTray();

            Log.Information("RabTrans started successfully");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to start main window");
            MessageBox.Show($"Failed to start RabTrans: {ex.Message}", "RabTrans", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var activateEvent = EventWaitHandle.OpenExisting(SingleInstanceActivateEventName);
            activateEvent.Set();
        }
        catch
        {
            // If the first instance is still starting, failing to signal should not spawn a second app.
        }
    }

    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceActivateEventName);
        _activateListenerCancellation = new CancellationTokenSource();
        var token = _activateListenerCancellation.Token;

        Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_activateEvent.WaitOne(250))
                    {
                        Dispatcher.Invoke(() => _mainWindow?.ShowInputTranslate());
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }, token);
    }

    private void SetupSystemTray()
    {
        _trayIcon = new TaskbarIcon
        {
            Icon = new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "RabTransIcon.ico")),
            ToolTipText = "RabTrans"
        };

        _trayIcon.TrayMouseDoubleClick += (_, _) => _mainWindow?.ShowInputTranslate();

        var menu = new ContextMenu();
        menu.Items.Add(CreateTrayMenuItem("Open Window", (_, _) => _mainWindow?.ShowInputTranslate()));
        menu.Items.Add(CreateTrayMenuItem("Screenshot Trans", async (_, _) =>
        {
            if (_mainWindow != null)
            {
                await _mainWindow.StartOcrAsync();
            }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateTrayMenuItem("Settings", (_, _) =>
        {
            if (_mainWindow == null)
            {
                return;
            }

            var settingsWindow = new SettingsWindow
            {
                Owner = _mainWindow
            };
            settingsWindow.ShowDialog();
        }));
        menu.Items.Add(CreateTrayMenuItem("Exit", (_, _) =>
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            Shutdown();
        }));

        _trayIcon.ContextMenu = menu;
        Log.Information("System tray initialized");
    }

    private static MenuItem CreateTrayMenuItem(string header, RoutedEventHandler clickHandler)
    {
        var item = new MenuItem
        {
            Header = header
        };
        item.Click += clickHandler;
        return item;
    }

    protected override void OnExit(ExitEventArgs args)
    {
        Log.Information("RabTrans shutting down...");
        
        // Cleanup services
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        _activateListenerCancellation?.Cancel();
        _activateListenerCancellation?.Dispose();
        _activateListenerCancellation = null;
        _activateEvent?.Dispose();
        _activateEvent = null;
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        Log.CloseAndFlush();
        base.OnExit(args);
    }

    /// <summary>
    /// Gets a service from the container.
    /// </summary>
    public static T GetService<T>() where T : class
    {
        return Services.GetRequiredService<T>();
    }
}
