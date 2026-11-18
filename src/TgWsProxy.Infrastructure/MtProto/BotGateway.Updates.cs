#nullable disable
using System.Globalization;
using System.Text;
using TgWsProxy.Infrastructure.MtProto.TL;
using TgWsProxy.Infrastructure.MtProto.TL.Abstractions;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>getUpdates: long-poll, updates.getDifference, очередь апдейтов и фильтр allowed_updates.</summary>
public sealed partial class BotGateway
{
    /// <summary>
    /// getUpdates: инкрементальная разница через updates.getState/getDifference с локальной очередью.
    /// <para>
    /// Telegram отдаёт разницу пачкой, а клиент просит не больше <c>limit</c> за вызов, поэтому
    /// всё, что не влезло, складывается в очередь бота: состояние <c>pts</c> уже сдвинуто и эти
    /// сообщения повторно не придут. <c>update_id</c> — сквозной счётчик сессии, не связанный
    /// с <c>Message.id</c> (тот локален для чата и после перезапуска начинается заново).
    /// </para>
    /// </summary>
    private async Task<BotApiResult> GetUpdatesAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        var offset = ReadIntArg(args, "offset", 0);
        var limit = ReadIntArg(args, "limit", 100);
        if (limit < 1)
        {
            limit = 1;
        }

        if (limit > 100)
        {
            limit = 100;
        }

        // long-poll: timeout — общее время ожидания новых сообщений (0 = вернуться сразу),
        // allowed_updates — фильтр типов (message / callback_query / inline_query / chosen_inline_result).
        var timeout = ReadIntArg(args, "timeout", 0);
        if (timeout < 0)
        {
            timeout = 0;
        }

        if (timeout > 60)
        {
            timeout = 60;
        }

        var allowed = ReadStringListArg(args, "allowed_updates");
        var deadline = timeout > 0 ? DateTime.UtcNow.AddSeconds(timeout) : (DateTime?)null;
        var queue = _pendingUpdates.GetOrAdd(botId, static _ => new UpdateQueue());

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // offset — подтверждение: всё, что клиент уже получил, из очереди убираем.
            queue.Acknowledge(offset, _store.Load(botId)?.UpdateId ?? 0);

            var session = client.ExportSession();
            var pts = (int)Math.Min(session.Pts, int.MaxValue);
            var qts = (int)Math.Min(session.Qts, int.MaxValue);
            var date = session.Date <= 0
                ? DateTime.UnixEpoch
                : DateTimeOffset.FromUnixTimeSeconds(session.Date).UtcDateTime;

            List<Dictionary<string, object>> fresh;

            // Сервер вправе прислать updates-контейнер вместо ответа на RPC (так доходят callback_query
            // и inline_query: у них нет pts, поэтому в updates.difference они не попадают). Клиент такие
            // ответы складывает в очередь — забираем и обрабатываем наравне с разницей updates.
            var (pushedFresh, resync) = BuildPushedUpdates(client.TakePushedUpdates());
            if (resync)
            {
                // updatesTooLong: состояние бесполезно, следующая итерация зафиксирует новую базу.
                client.ApplyUpdatesState(new Updates_State { pts = 0, qts = 0, date = DateTime.UnixEpoch, seq = 0 });
                _store.Save(botId, MergeUpdateId(client.ExportSession(), queue));
            }

            if (pts == 0)
            {
                // Первый запуск: фиксируем текущее состояние как базовую линию, история не отдаётся.
                await client.SyncUpdateStateAsync(ct).ConfigureAwait(false);
                _store.Save(botId, client.ExportSession());
                fresh = [];
            }
            else
            {
                var diff = await client.GetDifferenceAsync(pts, date, qts, ct).ConfigureAwait(false);
                switch (diff)
                {
                    case Updates_DifferenceTooLong tooLong:
                        client.ApplyUpdatesState(new Updates_State
                        {
                            pts = tooLong.pts,
                            qts = qts,
                            date = date,
                            seq = (int)session.Seq,
                        });
                        fresh = [];
                        break;

                    case Updates_DifferenceEmpty empty:
                        client.ApplyUpdatesState(new Updates_State
                        {
                            pts = pts,
                            qts = qts,
                            date = empty.date,
                            seq = empty.seq,
                        });
                        fresh = [];
                        break;

                    case Updates_Difference d:
                        fresh = BuildUpdates(d.new_messages, d.users, d.chats);
                        fresh.AddRange(BuildOtherUpdates(d.other_updates, d.users, d.chats));
                        client.ApplyUpdatesState(d.state);
                        break;

                    case Updates_DifferenceSlice s:
                        fresh = BuildUpdates(s.new_messages, s.users, s.chats);
                        fresh.AddRange(BuildOtherUpdates(s.other_updates, s.users, s.chats));
                        client.ApplyUpdatesState(s.intermediate_state);
                        break;

                    default:
                        return BotApiResult.Fail(500, "unexpected updates.difference subtype");
                }

                // Состояние применяем всегда, иначе следующая итерация запросит тот же диапазон.
                _store.Save(botId, MergeUpdateId(client.ExportSession(), queue));
            }

            if (pushedFresh.Count > 0)
            {
                // Апдейты из push-контейнера пришли раньше разницы updates — отдаём их первыми.
                fresh.InsertRange(0, pushedFresh);
            }

            if (fresh.Count > 0)
            {
                // Ставим в очередь ВСЁ: allowed_updates фильтрует только выдачу (см. TakeBatch).
                // Настоящий Bot API не подтверждает «нежелательные» апдейты, поэтому клиент с другим
                // allowed_updates в следующем вызове обязан их получить; если фильтровать здесь,
                // они исчезали бы навсегда (pts уже сдвинут).
                queue.Enqueue(fresh);
            }

            var batch = queue.TakeBatch(offset, (int)limit, allowed);
            if (batch.Count > 0)
            {
                _store.Save(botId, MergeUpdateId(client.ExportSession(), queue));
                return BotApiResult.Success(batch);
            }

            if (deadline == null || DateTime.UtcNow >= deadline)
            {
                return BotApiResult.Success(batch); // пустой список: нечего отдавать до таймаута
            }

            // Новых сообщений нет и timeout не истёк: короткая пауза и повтор getDifference.
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Пишет счётчик update_id из очереди в снимок сессии перед сохранением.</summary>
    private static BotSession MergeUpdateId(BotSession session, UpdateQueue queue)
    {
        session.UpdateId = queue.LastIssuedId;
        return session;
    }

    /// <summary>
    /// Преобразует новые сообщения из updates.difference в Bot API update-объекты <c>{message}</c>.
    /// <c>update_id</c> здесь не присваивается: его выдаёт <see cref="UpdateQueue"/> в порядке
    /// постановки, чтобы нумерация была сквозной и не зависела от локального <c>Message.id</c>.
    /// </summary>
    internal static List<Dictionary<string, object>> BuildUpdates(
        MessageBase[] newMessages,
        Dictionary<long, User> users,
        Dictionary<long, ChatBase> chats)
    {
        var result = new List<Dictionary<string, object>>();

        // Бот должен уметь отвечать по chat_id из update, не зная access_hash (как в настоящем Bot API):
        // запоминаем хэши всех участников, пришедших вместе с разницей updates.
        RememberUserAccessHashes(users);
        RememberChannelAccessHashes(chats);

        if (newMessages is { Length: > 0 })
        {
            foreach (var msg in newMessages)
            {
                if (msg is not Message m || m.id <= 0)
                {
                    continue; // MessageEmpty/MessageService и прочие пока не отображаются
                }

                result.Add(new Dictionary<string, object>
                {
                    ["message"] = BuildMessage(m, users, chats),
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Разбирает апдейты, которые сервер прислал вместо ответа на RPC (контейнеры <c>updates</c>,
    /// <c>updatesCombined</c>, <c>updateShort</c>). Именно так доходят <c>callback_query</c> и
    /// <c>inline_query</c>: у них нет pts, поэтому в <c>updates.difference</c> они не попадают.
    /// Раньше клиент падал на таком ответе (<c>InvalidCastException</c>) и терял нажатие кнопки,
    /// а транспорт после этого рассинхронизировался. <c>resync=true</c> означает <c>updatesTooLong</c>:
    /// состояние надо перечитать с нуля.
    /// </summary>
    private (List<Dictionary<string, object>> Updates, bool Resync) BuildPushedUpdates(List<IObject> pushes)
    {
        var result = new List<Dictionary<string, object>>();
        var resync = false;

        foreach (var push in pushes)
        {
            switch (push)
            {
                case Updates u:
                    result.AddRange(BuildPushedList(u.updates, u.users, u.chats));
                    break;

                case UpdatesCombined c:
                    result.AddRange(BuildPushedList(c.updates, c.users, c.chats));
                    break;

                case UpdateShort s:
                    result.AddRange(BuildPushedList([s.update], null, null));
                    break;

                case UpdatesTooLong:
                    resync = true;
                    break;
            }
        }

        return (result, resync);
    }

    /// <summary>
    /// Преобразует элементы push-контейнера в Bot API апдейты. В контейнере лежат <c>Update</c>
    /// (в том числе <c>updateNewMessage</c>), поэтому сообщения достаём из обёртки, а остальное
    /// отдаём общему мапперу <see cref="BuildOtherUpdates"/>.
    /// </summary>
    internal static List<Dictionary<string, object>> BuildPushedList(
        Update[] updates,
        Dictionary<long, User> users,
        Dictionary<long, ChatBase> chats)
    {
        var result = new List<Dictionary<string, object>>();

        if (updates is not { Length: > 0 })
        {
            return result;
        }

        // Как и в difference: запоминаем access_hash участников, иначе бот не сможет ответить по chat_id.
        RememberUserAccessHashes(users);
        RememberChannelAccessHashes(chats);

        foreach (var update in updates)
        {
            if (update is UpdateNewMessage { message: Message message } && message.id > 0)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["message"] = BuildMessage(message, users, chats),
                });
                continue;
            }

            result.AddRange(BuildOtherUpdates([update], users, chats));
        }

        return result;
    }

    /// <summary>
    /// Преобразует <c>other_updates</c> из updates.difference в Bot API update-объекты:
    /// <c>callback_query</c> (нажатие inline-кнопки) и <c>inline_query</c> (запрос из inline-режима).
    /// Раньше эти апдейты отбрасывались, из-за чего <c>answerCallbackQuery</c>/<c>answerInlineQuery</c>
    /// были реализованы, но недостижимы — бот не получал ни одного нажатия.
    /// </summary>
    internal static List<Dictionary<string, object>> BuildOtherUpdates(
        Update[] otherUpdates,
        Dictionary<long, User> users,
        Dictionary<long, ChatBase> chats)
    {
        var result = new List<Dictionary<string, object>>();

        if (otherUpdates is not { Length: > 0 })
        {
            return result;
        }

        foreach (var update in otherUpdates)
        {
            switch (update)
            {
                case UpdateBotCallbackQuery cb:
                    result.Add(new Dictionary<string, object>
                    {
                        ["callback_query"] = BuildCallbackQuery(cb, users, chats),
                    });
                    break;

                case UpdateBotInlineQuery iq:
                    result.Add(new Dictionary<string, object>
                    {
                        ["inline_query"] = BuildInlineQuery(iq, users),
                    });
                    break;

                case UpdateBotInlineSend chosen:
                    result.Add(new Dictionary<string, object>
                    {
                        ["chosen_inline_result"] = BuildChosenInlineResult(chosen, users),
                    });
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Bot API <c>CallbackQuery</c>. Отправленное сообщение локально недоступно (в апдейте есть
    /// только <c>msg_id</c>), поэтому <c>message</c> — минимальная заглушка с id и чатом:
    /// этого достаточно, чтобы бот ответил через <c>answerCallbackQuery</c> или отредактировал сообщение.
    /// </summary>
    private static Dictionary<string, object> BuildCallbackQuery(
        UpdateBotCallbackQuery cb,
        Dictionary<long, User> users,
        Dictionary<long, ChatBase> chats)
    {
        var query = new Dictionary<string, object>
        {
            ["id"] = cb.query_id.ToString(CultureInfo.InvariantCulture),
            ["chat_instance"] = cb.chat_instance.ToString(CultureInfo.InvariantCulture),
        };

        if (users != null && users.TryGetValue(cb.user_id, out var from) && from != null)
        {
            query["from"] = BuildUser(from);
        }

        // Bot API допускает callback_query без message (кнопка на inline-сообщении),
        // поэтому неразрешимый пир просто опускаем, а не отдаём chat: null.
        var chat = BuildChat(cb.peer, chats);
        if (chat != null)
        {
            query["message"] = new Dictionary<string, object>
            {
                ["message_id"] = cb.msg_id,
                ["date"] = 0,
                ["chat"] = chat,
            };
        }

        // data в MTProto — сырые байты; в Bot API это строка (колбэки бота всегда кладут туда UTF-8).
        if (cb.flags.HasFlag(UpdateBotCallbackQuery.Flags.has_data)
            && cb.data is { Length: > 0 } payload)
        {
            query["data"] = Encoding.UTF8.GetString(payload);
        }

        if (cb.flags.HasFlag(UpdateBotCallbackQuery.Flags.has_game_short_name))
        {
            query["game_short_name"] = cb.game_short_name ?? string.Empty;
        }

        return query;
    }

    /// <summary>Bot API <c>InlineQuery</c>.</summary>
    private static Dictionary<string, object> BuildInlineQuery(UpdateBotInlineQuery iq, Dictionary<long, User> users)
    {
        var query = new Dictionary<string, object>
        {
            ["id"] = iq.query_id.ToString(CultureInfo.InvariantCulture),
            ["query"] = iq.query ?? string.Empty,
            ["offset"] = iq.offset ?? string.Empty,
        };

        if (users != null && users.TryGetValue(iq.user_id, out var from) && from != null)
        {
            query["from"] = BuildUser(from);
        }

        if (iq.flags.HasFlag(UpdateBotInlineQuery.Flags.has_geo) && iq.geo is GeoPoint geo)
        {
            query["location"] = new Dictionary<string, object>
            {
                ["longitude"] = geo.lon,
                ["latitude"] = geo.lat,
            };
        }

        return query;
    }

    /// <summary>
    /// Bot API <c>ChosenInlineResult</c> — пользователь выбрал результат inline-запроса.
    /// <c>inline_message_id</c> не отдаём: MTProto <c>InputBotInlineMessageID</c> (dc_id + id + access_hash)
    /// не имеет обратного разбора в непрозрачную строку Bot API, а редактирование inline-сообщений
    /// этот шлюз не поддерживает (нужен chat_id), так что бесполезный идентификатор только вводил бы в заблуждение.
    /// </summary>
    private static Dictionary<string, object> BuildChosenInlineResult(
        UpdateBotInlineSend chosen,
        Dictionary<long, User> users)
    {
        var result = new Dictionary<string, object>
        {
            ["result_id"] = chosen.id ?? string.Empty,
            ["query"] = chosen.query ?? string.Empty,
        };

        if (users != null && users.TryGetValue(chosen.user_id, out var from) && from != null)
        {
            result["from"] = BuildUser(from);
        }

        return result;
    }

    /// <summary>Достаёт updates/users/chats из UpdatesBase (составной или короткий); при неудаче — null-массив.</summary>
    private static void SplitUpdates(
        UpdatesBase result,
        out Update[] updates,
        out Dictionary<long, User> users,
        out Dictionary<long, ChatBase> chats)
    {
        users = null;
        chats = null;
        updates = null;
        switch (result)
        {
            case Updates u:
                updates = u.updates;
                users = u.users;
                chats = u.chats;
                break;
            case UpdatesCombined c:
                updates = c.updates;
                users = c.users;
                chats = c.chats;
                break;
            case UpdateShort s:
                updates = [s.update];
                break;
        }
    }

    /// <summary>
    /// Фильтр allowed_updates. Тип апдейта — это ключ его объекта (<c>message</c>,
    /// <c>callback_query</c>, <c>inline_query</c>, <c>chosen_inline_result</c>), поэтому фильтр
    /// не зашивает список типов, а сверяет ключ с <paramref name="allowed"/>.
    /// <c>allowed_updates = ["message"]</c> по умолчанию у клиентов, поэтому фильтр обязателен.
    /// Применяется при выдаче (<see cref="UpdateQueue.TakeBatch"/>), а не при постановке в очередь:
    /// непрошеные апдейты не должны пропадать до подтверждения через <c>offset</c>.
    /// </summary>
    internal static List<Dictionary<string, object>> FilterAllowedUpdates(
        List<Dictionary<string, object>> updates, string[] allowed)
    {
        if (updates == null || updates.Count == 0)
        {
            return updates ?? [];
        }

        if (allowed == null || allowed.Length == 0)
        {
            return updates;
        }

        var result = new List<Dictionary<string, object>>();
        foreach (var u in updates)
        {
            if (IsAllowedUpdate(u, allowed))
            {
                result.Add(u);
            }
        }

        return result;
    }

    /// <summary>Проверяет, попадает ли тип апдейта в <paramref name="allowed"/> (пусто/null — все разрешены).</summary>
    internal static bool IsAllowedUpdate(Dictionary<string, object> update, string[] allowed)
    {
        if (allowed == null || allowed.Length == 0)
        {
            return true;
        }

        foreach (var key in update.Keys)
        {
            if (key == "update_id")
            {
                continue;
            }

            if (ArrayIndexOf(allowed, key) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int ArrayIndexOf(string[] items, string value)
    {
        for (var i = 0; i < items.Length; i++)
        {
            if (string.Equals(items[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Очередь невыданных Bot API-апдейтов одного бота. Экземпляр один на бота (живёт в
    /// <c>_pendingUpdates</c>), поэтому доступ синхронизирован внутренним локом: getUpdates может
    /// вызываться параллельно из нескольких HTTP-запросов.
    /// </summary>
    internal sealed class UpdateQueue
    {
        private readonly Lock _gate = new();
        private readonly Queue<Dictionary<string, object>> _items = new();

        /// <summary>Последний выданный сквозной <c>update_id</c>; 0 — счётчик ещё не запускался.</summary>
        public long LastIssuedId { get; private set; }

        /// <summary>
        /// Обрабатывает подтверждение клиента: <c>offset</c> в Bot API — это «следующий ожидаемый
        /// update_id», значит всё с меньшим id можно снять с головы очереди.
        /// </summary>
        public void Acknowledge(long offset, long persistedUpdateId)
        {
            lock (_gate)
            {
                if (LastIssuedId == 0 && persistedUpdateId > 0)
                {
                    // После рестарта процесса: продолжаем нумерацию с сохранённого значения,
                    // иначе клиент, помнящий старые id, отверг бы новые апдейты как дубли.
                    LastIssuedId = persistedUpdateId;
                }

                while (_items.Count > 0 && ReadUpdateId(_items.Peek()) < offset)
                {
                    _items.Dequeue();
                }

                if (offset - 1 > LastIssuedId)
                {
                    // Клиент подтвердил id, которых мы не выдавали (например сессия бота пересоздана).
                    // Подтягиваем счётчик, чтобы новые апдейты не считались уже доставленными.
                    LastIssuedId = offset - 1;
                }
            }
        }

        /// <summary>Ставит апдейты в очередь, присваивая каждому следующий <c>update_id</c>.</summary>
        public void Enqueue(IEnumerable<Dictionary<string, object>> updates)
        {
            lock (_gate)
            {
                foreach (var update in updates)
                {
                    update["update_id"] = ++LastIssuedId;
                    _items.Enqueue(update);
                }

                while (_items.Count > MaxPendingUpdates)
                {
                    _items.Dequeue(); // переполнение: самый старый апдейт теряется, см. предупреждение ниже
                }
            }
        }

        /// <summary>
        /// Забирает до <paramref name="limit"/> апдейтов начиная с <paramref name="offset"/>.
        /// Апдейты, не входящие в <paramref name="allowed"/>, пропускаются, но остаются в очереди:
        /// они снимаются только при подтверждении через <c>offset</c> — как в настоящем Bot API.
        /// </summary>
        public List<Dictionary<string, object>> TakeBatch(long offset, int limit, string[] allowed = null)
        {
            lock (_gate)
            {
                var batch = new List<Dictionary<string, object>>();
                foreach (var update in _items)
                {
                    if (batch.Count >= limit)
                    {
                        break;
                    }

                    if (ReadUpdateId(update) >= offset && IsAllowedUpdate(update, allowed))
                    {
                        batch.Add(update);
                    }
                }

                return batch;
            }
        }

        private static long ReadUpdateId(Dictionary<string, object> update) => update.TryGetValue("update_id", out var value) && value is long id ? id : 0;
    }
}
