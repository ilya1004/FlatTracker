using System.Threading.Channels;
using FlatTracker.Core.Models;
using FlatTracker.Infrastructure.Configuration;
using FlatTracker.UI.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TL;
using WTelegram;

namespace FlatTracker.UI.Workers;

public sealed class TelegramMonitorWorker(
    TelegramClientService telegramClientService,
    IOptions<TelegramOptions> options,
    MonitorStateStore stateStore,
    MessageDeduplicator dedup,
    Channel<QueuedMessage> messageQueue,
    ILogger<TelegramMonitorWorker> logger) : BackgroundService
{
    private readonly TelegramOptions _options = options.Value;
    private InputPeer? _targetPeer;
    private long _targetPeerId;
    private int _topicRootMessageId;
    private string? _topicTitle;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var client = telegramClientService.Client;

        if (string.IsNullOrWhiteSpace(_options.Group))
        {
            logger.LogError("Не задана настройка Telegram:Group — мониторинг не запущен.");
            return;
        }

        try
        {
            var user = await client.LoginUserIfNeeded();
            logger.LogInformation("Авторизован как {User} (id {Id})", user.username ?? user.first_name, user.ID);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Авторизация Telegram отменена, мониторинг не запущен");
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось авторизоваться в Telegram, мониторинг не запущен");
            return;
        }

        try
        {
            _targetPeer = await ResolveTargetPeerAsync(client);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось определить чат {Group}", _options.Group);
            return;
        }

        _targetPeerId = _targetPeer switch
        {
            InputPeerChannel ch => ch.ID,
            InputPeerChat chat => chat.ID,
            _ => 0
        };

        logger.LogInformation("Целевой чат: {Group} (peer id {PeerId})", _options.Group, _targetPeerId);

        if (_options.TopicId > 0)
        {
            await ResolveTopicAsync(client);
            logger.LogInformation(
                "Целевой топик форума: {Topic}, корневое сообщение {RootId}",
                TopicLabel, _topicRootMessageId);
        }

        var peerKey = _targetPeerId.ToString();

        await CatchUpMissedMessagesAsync(client, peerKey, ct);

        client.OnUpdates += OnUpdates;

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            client.OnUpdates -= OnUpdates;
        }
    }

    private async Task CatchUpMissedMessagesAsync(Client client, string peerKey, CancellationToken ct)
    {
        var lastId = stateStore.GetLastMessageId(peerKey);
        var limit = Math.Clamp(_options.CatchUpLimit, 1, 200);

        Messages_MessagesBase result;
        try
        {
            // min_id = lastId -> только то, что пришло после прошлого запуска
            result = await client.Messages_GetHistory(_targetPeer!, offset_id: 0, min_id: (int)Math.Min(lastId, int.MaxValue), limit: limit);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось получить историю чата (min_id {MinId})", lastId);
            return;
        }

        var history = ExtractHistory(result);

        if (history is null)
        {
            logger.LogWarning("История чата недоступна: {Type}", result.GetType().Name);
            return;
        }

        var queue = history
            .OfType<Message>()
            .Where(m => m.message is not null)
            .Where(IsFromTargetTopic)
            .OrderBy(m => m.id)
            .ToList();

        if (queue.Count == 0)
        {
            logger.LogInformation("Новых сообщений в истории нет (min_id {MinId})", lastId);
            return;
        }

        logger.LogInformation("Догоняем {Count} пропущенных сообщений", queue.Count);

        foreach (var msg in queue)
        {
            // Помечаем как виденные заранее: повторный прогон не должен слать их в LLM дважды.
            dedup.Seed(msg.message!);
            await stateStore.SetLastMessageIdAsync(peerKey, msg.id, ct);
        }

        foreach (var msg in queue)
        {
            logger.LogInformation("Новое сообщение #{Id} (из истории): {Preview}", msg.id, Preview(msg.message!));
            await EnqueueAsync(msg, ct);
        }
    }

    /// <summary>
    /// messages.getHistory для каналов отдаёт messages.channelMessages, а не messages.messages,
    /// поэтому оба контейнера приходят как равнозначные.
    /// </summary>
    private static MessageBase[]? ExtractHistory(Messages_MessagesBase result) => result switch
    {
        // Messages_MessagesSlice наследуется от Messages_Messages и попадает сюда же.
        Messages_Messages messages => messages.messages,
        Messages_ChannelMessages messages => messages.messages,
        _ => null
    };

    private Task OnUpdates(UpdatesBase updates)
    {
        _ = Task.Run(() => HandleUpdatesAsync(updates));
        return Task.CompletedTask;
    }

    private async Task HandleUpdatesAsync(UpdatesBase updates)
    {
        try
        {
            var dropped = 0;
            var otherGroup = 0;
            var otherTopic = 0;
            var tooShort = 0;
            string? samplePeer = null;
            int? sampleRootId = null;
            int? sampleTopId = null;

            foreach (var msg in ExtractMessages(updates))
            {
                var header = msg.reply_to as MessageReplyHeader;

                switch (await HandleMessageAsync(msg))
                {
                    case DropReason.None:
                        break;

                    case DropReason.OtherGroup:
                        otherGroup++;
                        dropped++;
                        samplePeer ??= DescribePeer(msg.peer_id);
                        break;

                    case DropReason.OtherTopic:
                        otherTopic++;
                        dropped++;
                        sampleRootId ??= header?.reply_to_msg_id;
                        sampleTopId ??= header?.reply_to_top_id;
                        break;

                    case DropReason.TooShort:
                        tooShort++;
                        dropped++;
                        break;
                }
            }

            if (dropped == 0)
                return;

            // Собираем только те причины, которые реально сработали: перечислять
            // нули оказалось неудобно — их принимали за длину сообщений.
            var reasons = new List<string>(3);

            if (otherGroup > 0)
            {
                reasons.Add(
                    $"не из {_options.Group} — {otherGroup} (peer: {samplePeer ?? "неизвестен"})");
            }

            if (otherTopic > 0)
            {
                reasons.Add(
                    $"не из топика {TopicLabel} — {otherTopic} "
                    + $"(reply_to_msg_id={Format(sampleRootId)}, reply_to_top_id={Format(sampleTopId)})");
            }

            if (tooShort > 0)
                reasons.Add($"короче {_options.MinMessageLength} симв. — {tooShort}");

            logger.LogInformation(
                "Отфильтровано обновлений: {Dropped} — {Reasons}", dropped, string.Join(" | ", reasons));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка обработки батча обновлений");
        }
    }

    private string TopicLabel => _options.TopicId <= 0
        ? "не задан"
        : _topicTitle is null ? $"#{_options.TopicId}" : $"«{_topicTitle}» (#{_options.TopicId})";

    private static string Format(int? value) =>
        value is null or 0 ? "нет" : value.Value.ToString();

    /// <summary>
    /// Разбирает UpdatesBase любого типа и возвращает все новые сообщения.
    /// </summary>
    private static IEnumerable<Message> ExtractMessages(UpdatesBase updates)
    {
        IEnumerable<Update>? batch = updates switch
        {
            Updates u => u.updates,
            UpdatesCombined uc => uc.updates,
            _ => null
        };

        if (batch is not null)
        {
            foreach (var update in batch)
            {
                if (update is UpdateNewMessage { message: Message m } && m.message is not null)
                    yield return m;
                else if (update is UpdateNewChannelMessage { message: Message cm } && cm.message is not null)
                    yield return cm;
            }

            yield break;
        }

        switch (updates)
        {
            case UpdateShortMessage usm:
                yield return new Message
                {
                    id = usm.id,
                    date = usm.date,
                    message = usm.message,
                    peer_id = new PeerUser { user_id = usm.user_id }
                };
                break;

            case UpdateShortChatMessage uscm:
                yield return new Message
                {
                    id = uscm.id,
                    date = uscm.date,
                    message = uscm.message,
                    peer_id = new PeerChat { chat_id = uscm.chat_id },
                    from_id = new PeerUser { user_id = uscm.from_id }
                };
                break;

            case UpdateShort { update: UpdateNewMessage { message: Message sm } }:
                yield return sm;
                break;

            default:
                break;
        }
    }

    private enum DropReason
    {
        None,
        OtherGroup,
        OtherTopic,
        TooShort
    }

    /// <summary>
    /// Возвращает причину, по которой сообщение не попало в очередь.
    /// </summary>
    private async Task<DropReason> HandleMessageAsync(Message msg)
    {
        if (!IsFromTargetGroup(msg))
        {
            logger.LogDebug("Сообщение #{Id} не из целевой группы: {Peer}, ожидали {Expected}",
                msg.id, DescribePeer(msg.peer_id), _targetPeerId);
            return DropReason.OtherGroup;
        }

        if (!IsFromTargetTopic(msg))
        {
            var header = msg.reply_to as MessageReplyHeader;
            logger.LogDebug(
                "Сообщение #{Id} не из топика {TopicId} (корень {RootId}): "
                + "reply_to_msg_id={RootId2}, reply_to_top_id={TopId}",
                msg.id, _options.TopicId, _topicRootMessageId, header?.reply_to_msg_id,
                header?.reply_to_top_id);
            return DropReason.OtherTopic;
        }

        if (string.IsNullOrWhiteSpace(msg.message) || msg.message.Length < _options.MinMessageLength)
        {
            logger.LogDebug("Сообщение #{Id} длиной {Len} короче MinMessageLength, пропущено",
                msg.id, msg.message?.Length ?? 0);
            return DropReason.TooShort;
        }

        var text = msg.message;
        logger.LogInformation("Новое сообщение #{Id}: {Preview}", msg.id, Preview(text));

        await stateStore.SetLastMessageIdAsync(_targetPeerId.ToString(), msg.id);
        await EnqueueAsync(msg, CancellationToken.None);
        return DropReason.None;
    }

    private static string DescribePeer(Peer? peer) => peer switch
    {
        PeerChannel ch => $"channel {ch.channel_id}",
        PeerChat chat => $"chat {chat.chat_id}",
        PeerUser user => $"user {user.user_id}",
        null => "нет peer",
        _ => peer.GetType().Name
    };

    private async Task EnqueueAsync(Message msg, CancellationToken ct)
    {
        try
        {
            await messageQueue.Writer.WriteAsync(
                new QueuedMessage(msg.message!, AdSource.TelegramGroup), ct);
        }
        catch (ChannelClosedException)
        {
        }
    }

    private bool IsFromTargetGroup(Message msg)
    {
        return msg.peer_id switch
        {
            PeerChannel ch => ch.channel_id == _targetPeerId,
            PeerChat chat => chat.chat_id == _targetPeerId,
            _ => false
        };
    }

    /// <summary>
    /// Сообщение внутри топика форума отвечает на корневое служебное сообщение
    /// топика, поэтому надёжный признак — <c>reply_to_msg_id == ForumTopic.top_message</c>.
    /// <c>reply_to_top_id</c> для обычных сообщений топика не заполняется (приходит 0)
    /// и годится только для ответов между разными топиками.
    /// </summary>
    private bool IsFromTargetTopic(Message msg)
    {
        if (_options.TopicId <= 0)
            return true;

        if (msg.reply_to is not MessageReplyHeader header)
            return false;

        if (_topicRootMessageId > 0 && header.reply_to_msg_id == _topicRootMessageId)
            return true;

        return header.reply_to_top_id == _options.TopicId;
    }

    private async Task ResolveTopicAsync(Client client)
    {
        try
        {
            var topics = await client.Messages_GetForumTopics(_targetPeer!, limit: 100);

            var match = topics.topics
                .OfType<ForumTopic>()
                .FirstOrDefault(t => t.id == _options.TopicId);

            if (match is null)
            {
                logger.LogWarning(
                    "Топик {TopicId} не найден среди топиков форума — фильтр по топику отключён",
                    _options.TopicId);
                return;
            }

            // id корневого служебного сообщения топика: на него отвечает каждое
            // сообщение внутри топика, поэтому это надёжнее, чем reply_to_top_id.
            _topicRootMessageId = match.top_message;
            _topicTitle = match.title;

            if (_topicRootMessageId == 0)
            {
                logger.LogWarning(
                    "У топика {TopicId} нет корневого сообщения — сопоставление по корню недоступно",
                    _options.TopicId);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось получить список топиков форума");
        }
    }

    private async Task<InputPeer> ResolveTargetPeerAsync(Client client)
    {
        var target = _options.Group.Trim();

        if (target.StartsWith('@') || (!target.StartsWith("-100") && long.TryParse(target, out _)))
        {
            var username = target.TrimStart('@');
            try
            {
                var resolved = await client.Contacts_ResolveUsername(username);
                if (resolved.Chat is TL.Channel channel)
                    return new InputPeerChannel(channel.ID, channel.access_hash);
                if (resolved.Chat is Chat chat)
                    return new InputPeerChat(chat.ID);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось разрешить {Target} по публичному имени, ищу среди диалогов", target);
            }
        }
        else if (target.StartsWith("-100"))
        {
            // Публичного имени нет — ищем среди диалогов по названию.
            var byTitle = await FindInDialogsAsync(client, target);
            if (byTitle is not null)
                return byTitle;
        }

        var dialogMatch = await FindInDialogsAsync(client, target);
        if (dialogMatch is not null)
            return dialogMatch;

        throw new InvalidOperationException(
            $"Чат '{target}' не найден. Укажите @username, -100id или точное название группы.");
    }

    private async Task<InputPeer?> FindInDialogsAsync(Client client, string target)
    {
        var dialogs = await client.Messages_GetAllDialogs();

        foreach (var entry in dialogs.chats)
        {
            if (entry.Value is TL.Channel ch)
            {
                var byId = target.StartsWith("-100") && ch.ID.ToString() == target[4..];
                var byTitle = ch.title?.Contains(target, StringComparison.OrdinalIgnoreCase) == true;
                var byUsername = ch.username is not null &&
                                 ("@" + ch.username).Equals(target, StringComparison.OrdinalIgnoreCase);

                if (byId || byTitle || byUsername)
                    return new InputPeerChannel(ch.ID, ch.access_hash);
            }
            else if (entry.Value is Chat c)
            {
                if (c.ID.ToString() == target ||
                    c.title?.Contains(target, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new InputPeerChat(c.ID);
                }
            }
        }

        return null;
    }

    private static string Preview(string text) =>
        text.Length <= 80 ? text : text[..80] + "…";
}
