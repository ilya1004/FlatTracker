using System.Globalization;
using System.IO;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace FlatTracker.UI.Services;

internal static class LoggerSetup
{
    private const string SectionName = "Serilog";
    private const int DefaultRetainedFileCount = 7;
    private const long DefaultFileSizeLimitMb = 32;

    private static readonly Lazy<IConfiguration> LazyConfiguration =
        new(() => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .Build());

    private static readonly Lazy<string> LazyLogDirectory = new(ResolveLogDirectory);

    public static string LogDirectory => LazyLogDirectory.Value;

    public static ILogger Build(UiLogSink uiSink)
    {
        var section = LazyConfiguration.Value.GetSection(SectionName);
        var minLevel = ParseLevel(section["MinimumLevel"]) ?? LogEventLevel.Information;
        var retained = ParseInt(section["RetainedFileCount"]) ?? DefaultRetainedFileCount;
        var sizeLimitMb = ParseInt(section["FileSizeLimitMb"]) ?? DefaultFileSizeLimitMb;

        return new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u4}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(LogDirectory, "flattracker-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retained,
                fileSizeLimitBytes: sizeLimitMb * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u4}] {SourceContext}{NewLine}    {Message:lj}{NewLine}{Exception}",
                shared: true)
            .WriteTo.Sink(uiSink)
            .CreateLogger();
    }

    private static string ResolveLogDirectory()
    {
        var configured = LazyConfiguration.Value.GetSection(SectionName)["LogDirectory"];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured);

            if (TryEnsureDirectory(full))
                return full;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlatTracker",
            "logs");

        return TryEnsureDirectory(fallback) ? fallback : Path.GetTempPath();
    }

    private static bool TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            var probe = Path.Combine(path, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static LogEventLevel? ParseLevel(string? value) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level) ? level : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : null;
}
