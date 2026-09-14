namespace FlatTracker.Infrastructure.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public long ChatId { get; set; }
}