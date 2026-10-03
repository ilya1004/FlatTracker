using System.IO;

namespace FlatTracker.UI.Services;

/// <summary>
/// Разрешает пути к рабочим данным приложения: база, сессия WTelegram,
/// настройки уведомлений и логи.
/// </summary>
/// <remarks>
/// В опубликованной сборке данные лежат в профиле пользователя
/// (<c>%LOCALAPPDATA%\FlatTracker</c>), а не рядом с exe: в Program Files
/// нет прав на запись, а при обновлении каталог приложения заменяется целиком
/// вместе с базой и сессией. В Debug сборке всё остаётся рядом с бинарником.
/// </remarks>
internal static class AppDataPaths
{
    /// <summary>Переопределяет корневой каталог данных, удобно для portable-режима.</summary>
    public const string EnvironmentVariable = "FLATTRACKER_DATA_DIR";

    private const string ApplicationFolderName = "FlatTracker";
    private const string DefaultDataFolderName = "output";

    private static readonly Lazy<string> LazyRoot = new(ResolveRoot);

    /// <summary>Корневой каталог данных приложения.</summary>
    public static string Root => LazyRoot.Value;

    /// <summary>
    /// Каталог для данных скрапера: база, настройки уведомлений и состояние монитора.
    /// Создаётся на диске.
    /// </summary>
    public static string ResolveOutputPath(string? configured)
    {
        var path = Resolve(configured, DefaultDataFolderName);

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Путь к файлу внутри каталога данных. Относительный путь не зависит
    /// от текущего рабочего каталога. Создаёт родительский каталог.
    /// </summary>
    public static string ResolveFile(string? configured, string defaultFileName)
    {
        var path = Resolve(configured, defaultFileName);
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        return path;
    }

    /// <summary>Подкаталог корня данных, создаётся на диске.</summary>
    public static string ResolveSubdirectory(string name)
    {
        var path = Path.Combine(Root, name);

        Directory.CreateDirectory(path);
        return path;
    }

    private static string Resolve(string? configured, string fallback)
    {
        var trimmed = configured?.Trim();

        // Явный абсолютный путь из конфигурации всегда выигрывает.
        if (!string.IsNullOrEmpty(trimmed) && Path.IsPathRooted(trimmed))
            return Path.GetFullPath(trimmed);

        // "./output" и "output/telegram.session" должны вести в одно и то же место,
        // поэтому ведущий "./" убираем, а остальное склеиваем с корнем данных.
        var relative = string.IsNullOrEmpty(trimmed)
            ? fallback
            : trimmed.TrimStart('.', '/', '\\');

        return Path.GetFullPath(Path.Combine(Root, relative));
    }

    private static string ResolveRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Path.GetFullPath(fromEnvironment.Trim());

#if DEBUG
        return AppContext.BaseDirectory;
#else
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName);
#endif
    }
}