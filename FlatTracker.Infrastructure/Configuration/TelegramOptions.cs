namespace FlatTracker.Infrastructure.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public long ChatId { get; set; }

    public int ApiId { get; set; }

    public string ApiHash { get; set; } = string.Empty;

    public string PhoneNumber { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// id топика форума (сообщения, на которые отвечают в топике).
    /// 0 или меньше — брать все сообщения группы.
    /// </summary>
    public int TopicId { get; set; }

    public string SessionPath { get; set; } = "telegram.session";

    /// <summary>Ключ шифрования файла сессии. Пустое значение — без шифрования.</summary>
    public string SessionKey { get; set; } = string.Empty;

    public int CatchUpLimit { get; set; } = 50;

    public int MinMessageLength { get; set; } = 40;
}
