using FlatTracker.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Windows;
using WTelegram;

namespace FlatTracker.UI.Services;

public sealed class TelegramClientService : IAsyncDisposable
{
    private const string ConfigApiId = "api_id";
    private const string ConfigApiHash = "api_hash";
    private const string ConfigPhoneNumber = "phone_number";
    private const string ConfigVerificationCode = "verification_code";
    private const string ConfigPassword = "password";
    private const string ConfigSessionPathname = "session_pathname";
    private const string ConfigSessionKey = "session_key";
    private const string ConfigLogOut = "log_out";
    private const string ConfigDeviceModel = "device_model";
    private const string ConfigDevicePlatform = "device_platform";
    private const string ConfigSystemVersion = "system_version";
    private const string ConfigAppVersion = "app_version";
    private const string ConfigSystemLangCode = "system_lang_code";
    private const string ConfigLangCode = "lang_code";
    private const string ConfigDatabaseDirectory = "database_directory";
    private const string ConfigDatabaseEncryptionKey = "database_encryption_key";

    private readonly Client _client;
    private readonly string _sessionFilePath;
    private readonly ILogger<TelegramClientService> _logger;

    public Client Client => _client;
    public TelegramClientService(IOptions<TelegramOptions> options, ILogger<TelegramClientService> logger)
    {
        _logger = logger;

        var opt = options.Value;

        if (opt.ApiId == 0 || string.IsNullOrWhiteSpace(opt.ApiHash))
        {
            throw new InvalidOperationException(
                "Не заданы Telegram:ApiId и Telegram:ApiHash. Укажите их в appsettings.Local.json.");
        }

        if (string.IsNullOrWhiteSpace(opt.PhoneNumber))
        {
            throw new InvalidOperationException("Не задан Telegram:PhoneNumber (с кодом страны, напр. +375...).");
        }

        _sessionFilePath = ResolveSessionPath(opt.SessionPath);
        _client = new Client(BuildConfig(opt));

        _logger.LogInformation("WTelegram, файл сессии: {SessionPath}", _sessionFilePath);
    }

    /// <summary>
    /// WTelegram запрашивает значения синхронно, а набор ключей меняется между версиями,
    /// поэтому неизвестные ключи отдаём null, а не бросаем исключение.
    /// </summary>
    /// <remarks>
    /// Ключи, к которым чувствителен разбор, возвращать как пустую строку нельзя:
    /// <c>session_key</c> — это hex-строка, и <c>Convert.FromHexString("")</c> даёт
    /// пустой массив, из-за чего AES падает с «Specified key is not a valid size».
    /// null заставляет WTelegram использовать <c>api_hash</c> (32 hex = 16 байт) — это штатный режим.
    /// </remarks>
    private Func<string, string?> BuildConfig(TelegramOptions opt) => what => what switch
    {
        ConfigApiId => opt.ApiId.ToString(),
        ConfigApiHash => opt.ApiHash,
        ConfigPhoneNumber => opt.PhoneNumber,
        ConfigVerificationCode => PromptForValue(
            $"Введите код подтверждения, отправленный в Telegram на номер {opt.PhoneNumber}", isSecret: false),
        ConfigPassword => PromptForValue("Введите облачный пароль Telegram (2FA)", isSecret: true),
        ConfigSessionPathname => _sessionFilePath,
        ConfigSessionKey => ResolveSessionKey(opt),
        ConfigLogOut => null,
        ConfigDeviceModel => Environment.MachineName,
        ConfigDevicePlatform => "Windows",
        ConfigSystemVersion => Environment.OSVersion.Version.ToString(),
        ConfigAppVersion => "FlatTracker",
        ConfigSystemLangCode => "ru",
        ConfigLangCode => "ru",
        ConfigDatabaseDirectory => null,
        ConfigDatabaseEncryptionKey => null,
        _ => HandleUnknownKey(what)
    };

    /// <summary>
    /// Нужна hex-строка длиной 16, 24 или 32 байта (32, 48 или 64 символа).
    /// Если ключ не задан, возвращаем null — WTelegram возьмёт api_hash.
    /// </summary>
    private static string? ResolveSessionKey(TelegramOptions opt)
    {
        var configured = opt.SessionKey?.Trim();

        if (string.IsNullOrEmpty(configured))
            return null;

        if (!IsHex(configured) || configured.Length is not (32 or 48 or 64))
        {
            throw new InvalidOperationException(
                "Telegram:SessionKey должен быть hex-строкой длиной 32, 48 или 64 символа (AES-128/192/256). " +
                "Оставьте значение пустым, чтобы использовать ApiHash.");
        }

        return configured;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            var isHexDigit = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHexDigit)
                return false;
        }

        return value.Length > 0;
    }

    private string? HandleUnknownKey(string what)
    {
        _logger.LogWarning("WTelegram запросил неизвестный ключ конфигурации {Key}, возвращаю null", what);
        return null;
    }

    private static string ResolveSessionPath(string configured)
    {
        var fileName = string.IsNullOrWhiteSpace(configured) ? "telegram.session" : configured;
        var full = Path.GetFullPath(fileName);
        var dir = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return full;
    }

    /// <summary>
    /// WTelegram вызывает конфигурацию синхронно из фонового потока, поэтому
    /// ввод возможен только через диспетчер WPF. Требует запущенного message loop,
    /// т.е. MainWindow должен быть показан до старта хоста.
    /// </summary>
    private string PromptForValue(string prompt, bool isSecret)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Нет UI-диспетчера: невозможно запросить данные авторизации.");

        var result = dispatcher.Invoke(() =>
        {
            var dialog = new CodeInputDialog(prompt, isSecret);
            return dialog.ShowDialog() == true ? dialog.Value : string.Empty;
        });

        if (string.IsNullOrWhiteSpace(result))
            throw new OperationCanceledException("Авторизация Telegram отменена пользователем.");

        return result.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        _client.OnUpdates -= null;
        await _client.DisposeAsync();
    }
}
