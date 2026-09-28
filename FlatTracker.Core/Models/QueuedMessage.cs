namespace FlatTracker.Core.Models;

/// <summary>Сообщение, ожидающее разбора LLM, вместе с источником.</summary>
public sealed record QueuedMessage(string Text, AdSource Source);
