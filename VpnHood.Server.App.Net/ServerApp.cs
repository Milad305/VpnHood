﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ga4.Ga4Tracking;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using VpnHood.Common;
using VpnHood.Common.Exceptions;
using VpnHood.Common.Logging;
using VpnHood.Common.Utils;
using VpnHood.Server.Access.Managers;
using VpnHood.Server.Access.Managers.File;
using VpnHood.Server.Access.Managers.Http;
using VpnHood.Server.App.SystemInformation;
using VpnHood.Server.SystemInformation;
using VpnHood.Tunneling;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace VpnHood.Server.App;

public class ServerApp : IDisposable
{
    private const string FileNamePublish = "publish.json";
    private const string FileNameAppCommand = "appcommand";
    private const string FolderNameStorage = "storage";
    private const string FolderNameInternal = "internal";
    private readonly Ga4Tracker _gaTracker;
    private readonly CommandListener _commandListener;
    private VpnHoodServer? _vpnHoodServer;
    private FileStream? _lockStream;
    private bool _disposed;

    public static string AppName => "VpnHoodServer";
    public static string AppFolderPath => Path.GetDirectoryName(typeof(ServerApp).Assembly.Location) ?? throw new Exception($"Could not acquire {nameof(AppFolderPath)}!");
    public AppSettings AppSettings { get; }
    public static string StoragePath => Directory.GetCurrentDirectory();
    public string InternalStoragePath { get; }

    public ServerApp()
    {
        VhLogger.Instance = VhLogger.CreateConsoleLogger();
        AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

        // set storage folder
        var parentAppFolderPath = Path.GetDirectoryName(Path.GetDirectoryName(typeof(ServerApp).Assembly.Location));
        var storagePath = (parentAppFolderPath != null && File.Exists(Path.Combine(parentAppFolderPath, FileNamePublish)))
            ? Path.Combine(parentAppFolderPath, FolderNameStorage)
            : Path.Combine(Directory.GetCurrentDirectory(), FolderNameStorage);
        Directory.CreateDirectory(storagePath);
        Directory.SetCurrentDirectory(storagePath);

        // internal folder
        InternalStoragePath = Path.Combine(storagePath, FolderNameInternal);
        Directory.CreateDirectory(InternalStoragePath);

        // load app settings
        var appSettingsFilePath = Path.Combine(StoragePath, "appsettings.debug.json");
        if (!File.Exists(appSettingsFilePath)) appSettingsFilePath = Path.Combine(StoragePath, "appsettings.json");
        if (!File.Exists(appSettingsFilePath)) //todo legacy for version 318 and older
        {
            var oldSettingsFile = Path.Combine(Path.GetDirectoryName(storagePath)!, "appsettings.json");
            if (File.Exists(oldSettingsFile))
            {
                appSettingsFilePath = Path.Combine(StoragePath, "appsettings.json");
                File.Copy(oldSettingsFile, appSettingsFilePath);
            }
        }
        if (!File.Exists(appSettingsFilePath)) appSettingsFilePath = Path.Combine(AppFolderPath, "appsettings.json");
        AppSettings = File.Exists(appSettingsFilePath)
            ? VhUtil.JsonDeserialize<AppSettings>(File.ReadAllText(appSettingsFilePath))
            : new AppSettings();
        VhLogger.IsDiagnoseMode = AppSettings.IsDiagnoseMode;

        //create command Listener
        _commandListener = new CommandListener(Path.Combine(storagePath, FileNameAppCommand));
        _commandListener.CommandReceived += CommandListener_CommandReceived;

        // tracker
        var anonyClientId = GetServerId(InternalStoragePath).ToString();
        _gaTracker = new Ga4Tracker
        {
            // ReSharper disable once StringLiteralTypo
            MeasurementId = "G-9SWLGEX6BT",
            ApiSecret = string.Empty,
            ClientId = anonyClientId,
            SessionId = Guid.NewGuid().ToString(),
            IsEnabled = AppSettings.AllowAnonymousTracker,
        };

        // create access server
        AccessManager = AppSettings.HttpAccessManager != null
            ? CreateHttpAccessManager(AppSettings.HttpAccessManager)
            : CreateFileAccessManager(StoragePath, AppSettings.FileAccessManager);
    }

    private void InitFileLogger()
    {
        var configFilePath = Path.Combine(StoragePath, "NLog.config");
        if (!File.Exists(configFilePath)) configFilePath = Path.Combine(AppFolderPath, "NLog.config");
        if (File.Exists(configFilePath))
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddNLog(configFilePath);
                if (AppSettings.IsDiagnoseMode)
                    builder.SetMinimumLevel(LogLevel.Trace);
            });
            LogManager.Configuration.Variables["mydir"] = StoragePath;
            VhLogger.Instance = loggerFactory.CreateLogger("NLog");
        }
        else
        {
            VhLogger.Instance.LogWarning("Could not find NLog file. ConfigFilePath: {configFilePath}", configFilePath);
        }
    }

    private void CurrentDomain_ProcessExit(object? sender, EventArgs e)
    {
        if (_vpnHoodServer != null)
        {
            VhLogger.Instance.LogInformation("Syncing all sessions and terminating the server...");
            _vpnHoodServer.SessionManager.SyncSessions().Wait();
            _vpnHoodServer.Dispose();
        }
    }

    public static Guid GetServerId(string storagePath)
    {
        var serverIdFile = Path.Combine(storagePath, "server-id");
        if (File.Exists(serverIdFile) && Guid.TryParse(File.ReadAllText(serverIdFile), out var serverId))
            return serverId;

        serverId = Guid.NewGuid();
        File.WriteAllText(serverIdFile, serverId.ToString());
        return serverId;
    }

    public static byte[] GetServerKey(string storagePath)
    {
        var serverKeyFile = Path.Combine(storagePath, "server-key");
        var serverKey = new byte[16];
        if (File.Exists(serverKeyFile) &&
            Convert.TryFromBase64String(File.ReadAllText(serverKeyFile), serverKey, out var bytesWritten)
            && bytesWritten == 16)
            return serverKey;

        serverKey = VhUtil.GenerateKey();
        File.WriteAllText(serverKeyFile, Convert.ToBase64String(serverKey));
        return serverKey;
    }

    public IAccessManager AccessManager { get; }
    public FileAccessManager? FileAccessManager => AccessManager as FileAccessManager;

    private static FileAccessManager CreateFileAccessManager(string storageFolderPath, FileAccessManagerOptions? options)
    {
        var accessManagerFolder = Path.Combine(storageFolderPath, "access");
        VhLogger.Instance.LogInformation($"Using FileAccessManager. AccessFolder: {accessManagerFolder}");
        var ret = new FileAccessManager(accessManagerFolder, options ?? new FileAccessManagerOptions());
        return ret;
    }

    private static HttpAccessManager CreateHttpAccessManager(HttpAccessManagerOptions options)
    {
        VhLogger.Instance.LogInformation("Initializing ResetAccessManager. BaseUrl: {BaseUrl}", options.BaseUrl);
        var httpAccessManager = new HttpAccessManager(options)
        {
            Logger = VhLogger.Instance,
            LoggerEventId = GeneralEventId.AccessManager
        };
        return httpAccessManager;
    }

    private void CommandListener_CommandReceived(object? sender, CommandReceivedEventArgs e)
    {
        if (!VhUtil.IsNullOrEmpty(e.Arguments) && e.Arguments[0] == "stop")
        {
            VhLogger.Instance.LogInformation("I have received the stop command!");
            _vpnHoodServer?.SessionManager.SyncSessions().Wait();
            _vpnHoodServer?.Dispose();
        }
    }

    private void StopServer(CommandLineApplication cmdApp)
    {
        cmdApp.Description = "Stop all instances of VpnHoodServer that running from this folder";
        cmdApp.OnExecute(() =>
        {
            VhLogger.Instance.LogInformation("Sending stop server request...");
            _commandListener.SendCommand("stop");
        });
    }

    private bool IsAnotherInstanceRunning()
    {
        var lockFile = Path.Combine(InternalStoragePath, "server.lock");
        try
        {
            _lockStream = File.OpenWrite(lockFile);
            var stream = new StreamWriter(_lockStream, leaveOpen: true);
            stream.WriteLine(DateTime.UtcNow);
            stream.Dispose();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private void StartServer(CommandLineApplication cmdApp)
    {
        cmdApp.Description = "Run the server (default command)";
        cmdApp.OnExecuteAsync(async (cancellationToken) =>
        {
            // LogAnonymizer is on by default
            VhLogger.IsAnonymousMode = AppSettings.ServerConfig?.LogAnonymizerValue ?? true;
            
            // find listener port
            if (IsAnotherInstanceRunning())
                throw new AnotherInstanceIsRunning();

            // check FileAccessManager
            if (FileAccessManager != null && FileAccessManager.AccessItem_LoadAll().Length == 0)
                VhLogger.Instance.LogWarning(
                    "There is no token in the store! Use the following command to create one:\n " +
                    "dotnet VpnHoodServer.dll gen -?");

            // Init File Logger before starting server; other log should be on console or other file
            InitFileLogger();
            if (AccessManager is HttpAccessManager httpAccessManager)
                httpAccessManager.Logger = VhLogger.Instance;

            // systemInfoProvider
            ISystemInfoProvider systemInfoProvider = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? new LinuxSystemInfoProvider()
                : new WinSystemInfoProvider();

            // run server
            _vpnHoodServer = new VpnHoodServer(AccessManager, new ServerOptions
            {
                GaTracker = _gaTracker,
                SystemInfoProvider = systemInfoProvider,
                StoragePath = InternalStoragePath,
                Config = AppSettings.ServerConfig
            });

            // Command listener
            _commandListener.Start();

            // start server
            await _vpnHoodServer.Start();
            while (_vpnHoodServer.State != ServerState.Disposed)
                await Task.Delay(1000, cancellationToken);
            return 0;
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        LogManager.Shutdown();
    }

    public async Task Start(string[] args)
    {
        // replace "/?"
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "/?")
                args[i] = "-?";

        // set default
        if (args.Length == 0) args = new[] { "start" };
        var cmdApp = new CommandLineApplication
        {
            AllowArgumentSeparator = true,
            Name = AppName,
            FullName = "VpnHood server",
            MakeSuggestionsInErrorMessage = true
        };

        cmdApp.HelpOption(true);
        cmdApp.VersionOption("-n|--version", VpnHoodServer.ServerVersion.ToString(3));

        cmdApp.Command("start", StartServer);
        cmdApp.Command("stop", StopServer);

        if (FileAccessManager != null)
            new FileAccessManagerCommand(FileAccessManager)
                .AddCommands(cmdApp);

        await cmdApp.ExecuteAsync(args);
    }
}