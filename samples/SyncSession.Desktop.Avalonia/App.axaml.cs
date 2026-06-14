using System;
using System.IO;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyncSession.Client.Database;
using SyncSession.Client.Engine;
using SyncSession.Client.Http;
using SyncSession.Client.Services;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Samples.Desktop.ViewModels;
using SyncSession.Samples.Desktop.Views;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Samples.Shared.Schema;

namespace SyncSession.Samples.Desktop;

/// <summary>
/// Application entry point and DI composition root.
/// </summary>
public class App : Application
{
    private IServiceProvider? _services;

    /// <inheritdoc/>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        if (Design.IsDesignMode)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _services = BuildServices();

        // Initialize SQLite schema on first run
        InitializeDatabase(_services);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void InitializeDatabase(IServiceProvider services)
    {
        var db = services.GetRequiredService<IClientDatabase>();
        var conn = db.GetConnectionAsync().GetAwaiter().GetResult();
        var ddl = SqliteSchemaHelper.GetCreateAllTablesSql(typeof(Customer).Assembly);
        conn.Execute(ddl);
        conn.Execute("PRAGMA journal_mode=WAL;");
    }

    private static IServiceProvider BuildServices()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var clientId  = Guid.Parse(config["Client:ClientId"]!);
        var deviceId  = Guid.Parse(config["Client:DeviceId"]!);
        var tenantId  = Guid.TryParse(config["Client:TenantId"], out var tid) ? tid : (Guid?)null;
        var userId    = config["Client:UserId"] ?? "DemoUser";
        var serverUrl = config["SyncServer:BaseUrl"] ?? "http://localhost:5000/api";
        var dbPath    = config["ClientDatabase:FilePath"] ?? "desktop-client.db";

        var syncConfig = new ClientSyncConfiguration
        {
            PushBatchSize = config.GetValue("SyncConfiguration:PushBatchSize", 1000),
            PullBatchSize = config.GetValue("SyncConfiguration:PullBatchSize", 1000)
        };
        syncConfig.RegisterTable<Customer>("Customers", priority: 1);
        syncConfig.RegisterTable<Product>("Products", priority: 1);
        syncConfig.RegisterTable<Order>("Orders", priority: 2);
        syncConfig.TenantId = tenantId;

        var services = new ServiceCollection();

        // Logging
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

        // HTTP
        services.AddHttpClient();

        // Database
        services.AddSingleton<IClientDatabase>(_ =>
        {
            var conn = new SqliteConnection($"Data Source={dbPath}");
            var db = new SqliteClientDatabase(conn);
            return db;
        });

        // Sync engine (concrete type — the coordinator depends on it).
        // Singleton is fine in this single-user demo (identity is fixed in appsettings).
        // In apps where users can log out / switch on the same machine, rebuild the engine
        // on login instead — it snapshots TenantId/UserDisplayName at Build() time.
        // See docs/getting-started.md → "Dependency injection".
        services.AddSingleton<ClientSyncEngine>(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var http = httpFactory.CreateClient();
            var serverApi = new HttpSyncServerApi(http, serverUrl, deviceId);
            var db = sp.GetRequiredService<IClientDatabase>();

            return ClientSyncEngineBuilder.Build(
                db, serverApi, deviceId,
                syncConfig);
        });

        // Expose the same instance as ISyncEngine (the control path)
        services.AddSingleton<ISyncEngine>(sp => sp.GetRequiredService<ClientSyncEngine>());

        // High-level coordinator (skip-if-offline + retry) — the convenience path
        // demonstrated by SyncStatusViewModel.
        services.AddSingleton<SyncCoordinator>(sp =>
            new SyncCoordinator(sp.GetRequiredService<ClientSyncEngine>()));

        // Config values needed by ViewModels
        services.AddSingleton(_ => new AppSettings(serverUrl, tenantId, userId));

        // ViewModels
        services.AddTransient<CustomersViewModel>();
        services.AddTransient<ProductsViewModel>();
        services.AddTransient<OrdersViewModel>();
        services.AddTransient<SyncStatusViewModel>();
        services.AddTransient<SyncLogViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
