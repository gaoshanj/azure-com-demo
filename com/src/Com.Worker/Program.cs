using System.Text.Json;
using Azure.Identity;
using Com.Shared.Abstractions;
using Com.Shared.Data;
using Com.Shared.Services;
using Com.Shared.Workers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        cfg.AddJsonFile("appsettings.json", optional: true);
        cfg.AddEnvironmentVariables();
    })
    .ConfigureServices((ctx, services) =>
    {
        var cfg = ctx.Configuration;
        var provider = (cfg["DB_PROVIDER"] ?? "sqlserver").ToLowerInvariant();

        ComDbContext.SqliteCompatMode = provider != "sqlserver";
        services.AddDbContext<ComDbContext>(o =>
        {
            if (provider == "sqlserver") o.UseSqlServer(cfg.GetConnectionString("Default"), s => s.EnableRetryOnFailure(3));
            else o.UseSqlite(cfg.GetConnectionString("Default") ?? "Data Source=com.db");
        });

        var storageMode = (cfg["STORAGE_MODE"] ?? "azure").ToLowerInvariant();
        if (storageMode == "azure") services.AddSingleton<IBlobStore, AzureBlobStore>();
        else services.AddSingleton<IBlobStore, LocalBlobStore>();

        services.AddMemoryCache();
        services.AddSingleton<ICacheStore, MemoryCacheStore>();
        services.AddSingleton<IPdfRenderer, QuestPdfRenderer>();
        services.AddScoped<INotifier, NotificationService>();
        services.AddSingleton<IAuditMirror, CosmosAuditMirror>();
        services.AddScoped<ContractWorkHandler>();
        services.AddScoped<IntakeWorkHandler>();
    })
    .Build();

await host.RunAsync();
