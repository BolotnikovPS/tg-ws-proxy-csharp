using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Test;

/// <summary>
/// Проверяет очередь апдейтов, которая выдаёт сквозные <c>update_id</c>.
/// Раньше <c>update_id</c> брался из MTProto <c>Message.id</c>, а он локален для чата:
/// после <c>offset</c> клиент молча терял сообщения из чатов с меньшими id.
/// </summary>
public sealed class BotGatewayUpdateQueueTests
{
    private static Dictionary<string, object> Update(string text)
    {
        return new Dictionary<string, object>
        {
            ["message"] = new Dictionary<string, object> { ["text"] = text },
        };
    }

    private static long IdOf(Dictionary<string, object> update)
    {
        return (long)update["update_id"];
    }

    [Fact]
    public void Enqueue_AssignsMonotonicIds_StartingFromOne()
    {
        var queue = new BotGateway.UpdateQueue();

        queue.Enqueue([Update("a"), Update("b")]);
        queue.Enqueue([Update("c")]);

        var batch = queue.TakeBatch(0, 100);

        Assert.Equal(3, batch.Count);
        Assert.Equal([1L, 2L, 3L], batch.Select(IdOf).ToArray());
        Assert.Equal(3L, queue.LastIssuedId);
    }

    [Fact]
    public void Enqueue_IdsDoNotDependOnMessageId()
    {
        var queue = new BotGateway.UpdateQueue();

        // Порядок прихода: сообщение с большим Message.id первым, затем с маленьким.
        var first = new Dictionary<string, object>
        {
            ["message"] = new Dictionary<string, object> { ["message_id"] = 900 },
        };
        var second = new Dictionary<string, object>
        {
            ["message"] = new Dictionary<string, object> { ["message_id"] = 7 },
        };

        queue.Enqueue([first, second]);
        var batch = queue.TakeBatch(0, 10);

        Assert.Equal([1L, 2L], batch.Select(IdOf).ToArray());
        Assert.Equal(900, (int)((Dictionary<string, object>)batch[0]["message"])["message_id"]);
        Assert.Equal(7, (int)((Dictionary<string, object>)batch[1]["message"])["message_id"]);
    }

    [Fact]
    public void TakeBatch_WithSmallLimit_KeepsRemainingItemsForNextCall()
    {
        var queue = new BotGateway.UpdateQueue();
        queue.Enqueue([Update("a"), Update("b"), Update("c"), Update("d"), Update("e")]);

        var firstCall = queue.TakeBatch(0, 2);
        Assert.Equal([1L, 2L], firstCall.Select(IdOf).ToArray());

        // Клиент подтверждает полученное и просит следующую порцию.
        var offset = IdOf(firstCall[^1]) + 1;
        queue.Acknowledge(offset, 0);
        var secondCall = queue.TakeBatch(offset, 2);

        Assert.Equal([3L, 4L], secondCall.Select(IdOf).ToArray());
    }

    [Fact]
    public void Acknowledge_DropsConfirmedAndKeepsPending()
    {
        var queue = new BotGateway.UpdateQueue();
        queue.Enqueue([Update("a"), Update("b"), Update("c")]);

        queue.Acknowledge(3, 0); // клиент подтвердил update_id 1 и 2
        var batch = queue.TakeBatch(3, 10);

        Assert.Single(batch);
        Assert.Equal(3L, IdOf(batch[0]));
    }

    [Fact]
    public void Acknowledge_RestoresCounterFromPersistedValue()
    {
        var queue = new BotGateway.UpdateQueue();

        // После рестарта процесса нумерация продолжается с сохранённого значения,
        // иначе клиент, помнящий старые id, счёл бы новые апдейты дублями.
        queue.Acknowledge(0, persistedUpdateId: 500);
        queue.Enqueue([Update("a")]);

        Assert.Equal(501L, queue.LastIssuedId);
        Assert.Equal(501L, IdOf(queue.TakeBatch(0, 10)[0]));
    }

    [Fact]
    public void Acknowledge_OffsetAheadOfIssuedIds_RaisesCounter()
    {
        var queue = new BotGateway.UpdateQueue();
        queue.Enqueue([Update("a")]);

        // Клиент подтвердил id, которых мы не выдавали (например сессия бота пересоздана):
        // новые апдейты не должны попадать под уже подтверждённый offset.
        queue.Acknowledge(1000, 0);
        queue.Enqueue([Update("b")]);

        Assert.Equal(1000L, queue.LastIssuedId);
    }

    [Fact]
    public void Enqueue_BeyondCap_DropsOldestAndKeepsNewest()
    {
        var queue = new BotGateway.UpdateQueue();

        // Переполнение очереди (бот не забирает апдейты): сохраняем самые свежие.
        for (var i = 0; i < 10_005; i++)
        {
            queue.Enqueue([Update($"m{i}")]);
        }

        var batch = queue.TakeBatch(0, 20_000);

        Assert.Equal(10_000, batch.Count);
        Assert.Equal(6L, IdOf(batch[0]));
        Assert.Equal(10_005L, IdOf(batch[^1]));
    }

    private static Dictionary<string, object> CallbackUpdate(string data)
    {
        return new Dictionary<string, object>
        {
            ["callback_query"] = new Dictionary<string, object> { ["data"] = data },
        };
    }

    [Fact]
    public void TakeBatch_SkipsNotAllowedUpdates_ButKeepsThemForLaterCalls()
    {
        var queue = new BotGateway.UpdateQueue();
        queue.Enqueue([Update("a"), CallbackUpdate("cb")]);

        // Клиент просит только message: callback_query не отдаётся, но и не теряется.
        var messagesOnly = queue.TakeBatch(0, 10, ["message"]);
        Assert.Single(messagesOnly);
        Assert.Equal(1L, IdOf(messagesOnly[0]));

        // Следующий вызов с другим allowed_updates обязан получить callback_query.
        var callbacksOnly = queue.TakeBatch(0, 10, ["callback_query"]);
        Assert.Single(callbacksOnly);
        Assert.Equal(2L, IdOf(callbacksOnly[0]));

        // И до подтверждения через offset он остаётся доступным.
        Assert.Single(queue.TakeBatch(0, 10, ["callback_query"]));
    }

    [Fact]
    public void TakeBatch_AcknowledgeDropsNotAllowedUpdates_Too()
    {
        var queue = new BotGateway.UpdateQueue();
        queue.Enqueue([Update("a"), CallbackUpdate("cb")]);

        // Клиент подтвердил всё до 3-го id: непрошеный callback_query тоже снимается,
        // как в Bot API — offset подтверждает весь диапазон, а не только выданное.
        queue.Acknowledge(3, 0);
        Assert.Empty(queue.TakeBatch(0, 10, ["callback_query"]));
    }

    [Fact]
    public void TakeBatch_WithoutAllowed_ReturnsEverything()
    {
        var queue = new BotGateway.UpdateQueue();
        queue.Enqueue([Update("a"), CallbackUpdate("cb")]);

        Assert.Equal(2, queue.TakeBatch(0, 10, null).Count);
        Assert.Equal(2, queue.TakeBatch(0, 10, []).Count);
    }

    [Fact]
    public void IsAllowedUpdate_IgnoresUpdateIdKey()
    {
        var update = Update("a");
        update["update_id"] = 5L;

        // Сам update_id не является типом апдейта и не должен матчиться с allowed_updates.
        Assert.False(BotGateway.IsAllowedUpdate(update, ["update_id"]));
        Assert.True(BotGateway.IsAllowedUpdate(update, ["message"]));
        Assert.True(BotGateway.IsAllowedUpdate(update, null!));
    }
}
