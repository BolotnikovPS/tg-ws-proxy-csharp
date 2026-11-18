#nullable disable
using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>Отправка сообщений и медиа: sendMessage, sendRichMessage, sendPhoto/…, sendMediaGroup.</summary>
public sealed partial class BotGateway
{
    /// <summary>sendMessage: резолв пира → messages.sendMessage → Bot API message из результата Updates.</summary>
    private async Task<BotApiResult> SendMessageAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var text = ReadStringArg(args, "text");
        if (string.IsNullOrEmpty(text))
        {
            return BotApiResult.Fail(400, "text is required for sendMessage");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        var self = await client.GetUsersAsync([new InputUserSelf()], ct).ConfigureAwait(false);
        if (self is not { Length: > 0 } || self[0] is not User selfUser)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var peer = await ResolvePeerAsync(client, args, selfUser.id, ct).ConfigureAwait(false);
        var (message, entities) = ResolveTextAndEntities(args, text);

        var request = new Messages_SendMessage
        {
            flags = 0,
            peer = peer,
            message = message,
            random_id = NextRandomId(),
        };

        if (ReadSilentFlag(args))
        {
            request.flags |= Messages_SendMessage.Flags.silent;
        }

        if (ReadNoForwardsFlag(args))
        {
            request.flags |= Messages_SendMessage.Flags.noforwards;
        }

        if (ReadNoWebpageFlag(args))
        {
            request.flags |= Messages_SendMessage.Flags.no_webpage;
        }

        var replyTo = ReadReplyToArg(args);
        if (replyTo != null)
        {
            request.flags |= Messages_SendMessage.Flags.has_reply_to;
            request.reply_to = replyTo;
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= Messages_SendMessage.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        if (entities is { Length: > 0 })
        {
            request.flags |= Messages_SendMessage.Flags.has_entities;
            request.entities = entities;
        }

        var result = await client.InvokeAsync<UpdatesBase>(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(BuildSentMessage(result, peer, selfUser, message));
    }

    /// <summary>
    /// sendRichMessage (Bot API 10.1): rich_message строится из <c>rich_message.html</c>
    /// (inputRichMessageHTML) либо <c>rich_message.markdown</c> (inputRichMessageMarkdown).
    /// Текстовая подпись у таких сообщений отсутствует, поэтому в ответе текста нет.
    /// </summary>
    private async Task<BotApiResult> SendRichMessageAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var richMessage = ReadRichMessageArg(args);
        if (richMessage == null)
        {
            return BotApiResult.Fail(400, "rich_message is required for sendRichMessage");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        var selfUser = await GetSelfUserAsync(client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var peer = await ResolvePeerAsync(client, args, selfUser.id, ct).ConfigureAwait(false);

        var request = new Messages_SendMessage
        {
            flags = Messages_SendMessage.Flags.has_rich_message,
            peer = peer,
            message = string.Empty,
            random_id = NextRandomId(),
            rich_message = richMessage,
        };

        // У rich-сообщения нет текстового тела, поэтому no_webpage здесь неприменим;
        // silent/noforwards — те же, что и у sendMessage.
        if (ReadSilentFlag(args))
        {
            request.flags |= Messages_SendMessage.Flags.silent;
        }

        if (ReadNoForwardsFlag(args))
        {
            request.flags |= Messages_SendMessage.Flags.noforwards;
        }

        var replyTo = ReadReplyToArg(args);
        if (replyTo != null)
        {
            request.flags |= Messages_SendMessage.Flags.has_reply_to;
            request.reply_to = replyTo;
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= Messages_SendMessage.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        var result = await client.InvokeAsync<UpdatesBase>(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(BuildSentMessage(result, peer, selfUser, string.Empty));
    }

    /// <summary>Тип отправляемого медиа — нужен для выбора InputMedia* и DocumentAttribute*.</summary>
    private enum MediaType
    {
        Photo,
        Document,
        Audio,
        Video,
        Animation,
        Voice,
        VideoNote,
        Sticker,
    }

    private Task<BotApiResult> SendPhotoAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "photo", MediaType.Photo, "sendPhoto");

    private Task<BotApiResult> SendDocumentAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "document", MediaType.Document, "sendDocument");

    private Task<BotApiResult> SendAudioAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "audio", MediaType.Audio, "sendAudio");

    private Task<BotApiResult> SendVideoAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "video", MediaType.Video, "sendVideo");

    private Task<BotApiResult> SendAnimationAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "animation", MediaType.Animation, "sendAnimation");

    private Task<BotApiResult> SendVoiceAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "voice", MediaType.Voice, "sendVoice");

    private Task<BotApiResult> SendVideoNoteAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "video_note", MediaType.VideoNote, "sendVideoNote");

    private Task<BotApiResult> SendStickerAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "sticker", MediaType.Sticker, "sendSticker");

    /// <summary>Общий путь отправки одного медиа: upload (multipart) либо file_id, подпись, reply_to, reply_markup.</summary>
    private async Task<BotApiResult> SendSingleMediaAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct,
        string paramName, MediaType kind, string methodName)
    {
        var media = ReadMediaArg(args, paramName);
        if (media == null)
        {
            return BotApiResult.Fail(400, $"{paramName} is required for {methodName}");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        var self = await client.GetUsersAsync([new InputUserSelf()], ct).ConfigureAwait(false);
        if (self is not { Length: > 0 } || self[0] is not User selfUser)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var peer = await ResolvePeerAsync(client, args, selfUser.id, ct).ConfigureAwait(false);

        InputMedia inputMedia;
        if (media is string fileId)
        {
            inputMedia = BuildReusedMedia(kind, fileId);
        }
        else
        {
            var upload = (BotUploadFile)media;
            var file = await UploadMediaAsync(client, upload, ct).ConfigureAwait(false);
            inputMedia = BuildUploadedMedia(kind, upload, file, args);
        }

        var (caption, entities) = ResolveTextAndEntities(args, ReadStringArg(args, "caption"));
        var request = new Messages_SendMedia
        {
            flags = 0,
            peer = peer,
            media = inputMedia,
            message = caption ?? "",
            random_id = NextRandomId(),
        };

        if (ReadSilentFlag(args))
        {
            request.flags |= Messages_SendMedia.Flags.silent;
        }

        if (ReadNoForwardsFlag(args))
        {
            request.flags |= Messages_SendMedia.Flags.noforwards;
        }

        var replyTo = ReadReplyToArg(args);
        if (replyTo != null)
        {
            request.flags |= Messages_SendMedia.Flags.has_reply_to;
            request.reply_to = replyTo;
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= Messages_SendMedia.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        if (entities is { Length: > 0 })
        {
            request.flags |= Messages_SendMedia.Flags.has_entities;
            request.entities = entities;
        }

        var result = await client.InvokeAsync<UpdatesBase>(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(BuildSentMessage(result, peer, selfUser, caption ?? ""));
    }

    /// <summary>sendMediaGroup: массив медиа через messages.sendMultiMedia; одно сообщение на элемент альбома.</summary>
    private async Task<BotApiResult> SendMediaGroupAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var (mediaError, items) = ValidateMediaGroupItems(args);
        if (mediaError != null)
        {
            return BotApiResult.Fail(400, mediaError);
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        var self = await client.GetUsersAsync([new InputUserSelf()], ct).ConfigureAwait(false);
        if (self is not { Length: > 0 } || self[0] is not User selfUser)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var peer = await ResolvePeerAsync(client, args, selfUser.id, ct).ConfigureAwait(false);

        var multi = new List<InputSingleMedia>();
        foreach (var item in items)
        {
            multi.Add(await BuildSingleMediaAsync(client, peer, item, args, ct, album: true).ConfigureAwait(false));
        }

        if (multi.Count == 0)
        {
            return BotApiResult.Fail(400, "media must contain at least one item for sendMediaGroup");
        }

        var request = new Messages_SendMultiMedia
        {
            flags = 0,
            peer = peer,
            multi_media = [.. multi],
        };

        if (ReadSilentFlag(args))
        {
            request.flags |= Messages_SendMultiMedia.Flags.silent;
        }

        if (ReadNoForwardsFlag(args))
        {
            request.flags |= Messages_SendMultiMedia.Flags.noforwards;
        }

        var replyTo = ReadReplyToArg(args);
        if (replyTo != null)
        {
            request.flags |= Messages_SendMultiMedia.Flags.has_reply_to;
            request.reply_to = replyTo;
        }

        var result = await client.InvokeAsync<UpdatesBase>(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(ExtractSentMessages(result, selfUser));
    }

    /// <summary>
    /// Доводит локально загруженное медиа до вида, пригодного для альбома: прогоняет его через
    /// <c>messages.uploadMedia</c> и возвращает <c>inputMediaPhoto</c>/<c>inputMediaDocument</c> с
    /// готовым <c>photo</c>/<c>document</c>. Без этого <c>messages.sendMultiMedia</c> отвечает
    /// <c>MEDIA_INVALID</c> на <c>inputMediaUploaded*</c> (в одиночном <c>messages.sendMedia</c> они
    /// допустимы).
    /// </summary>
    private static async Task<InputMedia> PrepareAlbumMediaAsync(
        MtProtoClient client, InputPeer peer, InputMedia uploaded, CancellationToken ct)
    {
        var result = await client
            .InvokeAsync<MessageMedia>(new Messages_UploadMedia { flags = 0, peer = peer, media = uploaded }, ct)
            .ConfigureAwait(false);

        return result switch
        {
            MessageMediaPhoto { photo: Photo photo } => new InputMediaPhoto { flags = 0, id = photo },
            MessageMediaDocument { document: Document document } => new InputMediaDocument { flags = 0, id = document },
            _ => throw new BotGatewayException(
                500, $"messages.uploadMedia returned unsupported media '{result?.GetType().Name}'"),
        };
    }

    /// <summary>Собирает один элемент альбома (InputSingleMedia): upload по attach://… либо file_id.</summary>
    private static async Task<InputSingleMedia> BuildSingleMediaAsync(
        MtProtoClient client, InputPeer peer, JsonElement item, IReadOnlyDictionary<string, object> args,
        CancellationToken ct, bool album = false)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new BotGatewayException(400, "each sendMediaGroup item must be an object");
        }

        var kind = MediaKindOf(GetJsonString(item, "type"));
        var mediaRef = GetJsonString(item, "media");
        if (string.IsNullOrEmpty(mediaRef))
        {
            throw new BotGatewayException(400, "'media' is required for a sendMediaGroup item");
        }

        InputMedia inputMedia;
        if (mediaRef.StartsWith("attach://", StringComparison.Ordinal))
        {
            var key = mediaRef["attach://".Length..];
            if (ReadMediaArg(args, key) is not BotUploadFile upload)
            {
                throw new BotGatewayException(400, $"attached media '{mediaRef}' was not found in the request");
            }

            var file = await UploadMediaAsync(client, upload, ct).ConfigureAwait(false);
            var uploaded = BuildGroupUploadedMedia(kind, upload, file, item);
            // В альбоме uploadMedia обязателен (иначе MEDIA_INVALID), в одиночных методах — лишний RPC.
            inputMedia = album
                ? await PrepareAlbumMediaAsync(client, peer, uploaded, ct).ConfigureAwait(false)
                : uploaded;
        }
        else
        {
            inputMedia = BuildReusedMedia(kind, mediaRef);
        }

        var (caption, entities) = ResolveCaptionAndEntities(item);
        var single = new InputSingleMedia
        {
            flags = 0,
            media = inputMedia,
            random_id = NextRandomId(),
            message = caption ?? "",
        };

        if (entities is { Length: > 0 })
        {
            single.flags |= InputSingleMedia.Flags.has_entities;
            single.entities = entities;
        }

        return single;
    }

    private static MediaType MediaKindOf(string type) => type switch
    {
        "photo" => MediaType.Photo,
        "video" => MediaType.Video,
        "document" => MediaType.Document,
        "audio" => MediaType.Audio,
        _ => throw new BotGatewayException(400, $"unsupported media type '{type}' in sendMediaGroup"),
    };

    /// <summary>
    /// Вид медиа для проверки однородности альбома. Telegram объединяет в один альбом только
    /// фото и видео; документы и аудио образуют собственные группы (смесь даёт <c>MEDIA_INVALID</c>).
    /// </summary>
    private static string MediaFamilyOf(MediaType kind) => kind switch
    {
        MediaType.Photo or MediaType.Video => "photo/video",
        MediaType.Document => "document",
        MediaType.Audio => "audio",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Проверяет состав альбома по правилам Bot API до сети и до загрузки файлов: элементы должны быть
    /// объектами, их 2–10 штук и все они одного вида. Telegram отвечает <c>MEDIA_INVALID</c> и на один
    /// элемент, и на смесь фото с документом, поэтому такие запросы отсекаем сами — с понятным текстом.
    /// Возвращает текст ошибки для клиента (или null) и разобранный массив элементов.
    /// </summary>
    internal static (string Error, JsonElement[] Items) ValidateMediaGroupItems(IReadOnlyDictionary<string, object> args)
    {
        var mediaArray = ReadJsonArrayArg(args, "media");
        if (mediaArray == null)
        {
            return ("media is required for sendMediaGroup", []);
        }

        var items = mediaArray.Value.EnumerateArray().ToArray();
        if (items.Length is < 2 or > 10)
        {
            return ("media group must include 2-10 items", items);
        }

        string family = null;
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return ("each sendMediaGroup item must be an object", items);
            }

            var itemFamily = MediaFamilyOf(MediaKindOf(GetJsonString(item, "type")));
            family ??= itemFamily;
            if (!string.Equals(family, itemFamily, StringComparison.Ordinal))
            {
                return ("media group must contain only one kind of media: photos/videos, documents or audio", items);
            }
        }

        return (null, items);
    }

    /// <summary>Извлекает все сообщения альбома из результата sendMultiMedia в Bot API message-объекты.</summary>
    private static Dictionary<string, object>[] ExtractSentMessages(UpdatesBase result, User self)
    {
        Dictionary<long, User> users = null;
        Dictionary<long, ChatBase> chats = null;

        Update[] updates;
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
            case UpdateShort s when s.update is UpdateNewMessage:
                updates = [s.update];
                break;
            default:
                throw new BotGatewayException(500, $"unsupported sendMediaGroup result type '{result?.GetType().Name}'");
        }

        var list = new List<Dictionary<string, object>>();
        if (updates != null)
        {
            foreach (var u in updates)
            {
                if (u is UpdateNewMessage unm && unm.message is Message m && m.id > 0)
                {
                    var message = BuildMessage(m, users, chats);
                    if (self != null)
                    {
                        message["from"] = BuildUser(self);
                    }

                    list.Add(message);
                }
            }
        }

        if (list.Count == 0)
        {
            throw new BotGatewayException(500, "sendMediaGroup returned no message updates");
        }

        return [.. list];
    }

    /// <summary>Повторная отправка уже загруженного медиа по file_id (InputMediaPhoto / InputMediaDocument).</summary>
    private static InputMedia BuildReusedMedia(MediaType kind, string fileId)
        => kind == MediaType.Photo
            ? new InputMediaPhoto { flags = 0, id = DecodeInputPhoto(fileId) }
            : new InputMediaDocument { flags = 0, id = DecodeInputDocument(fileId) };

    /// <summary>Новое медиа из загруженного файла (InputMediaUploadedPhoto / InputMediaUploadedDocument).</summary>
    private static InputMedia BuildUploadedMedia(
        MediaType kind, BotUploadFile upload, InputFileBase file, IReadOnlyDictionary<string, object> args)
    {
        if (kind == MediaType.Photo)
        {
            return new InputMediaUploadedPhoto { flags = 0, file = file };
        }

        var mime = string.IsNullOrEmpty(upload.MimeType) ? "application/octet-stream" : upload.MimeType;
        return new InputMediaUploadedDocument
        {
            flags = 0,
            file = file,
            mime_type = mime,
            attributes = BuildDocumentAttributes(kind, upload, args),
        };
    }

    /// <summary>Документное медиа в альбоме (параметры читаются из JSON-элемента).</summary>
    private static InputMedia BuildGroupUploadedMedia(
        MediaType kind, BotUploadFile upload, InputFileBase file, JsonElement item)
    {
        if (kind == MediaType.Photo)
        {
            return new InputMediaUploadedPhoto { flags = 0, file = file };
        }

        var mime = string.IsNullOrEmpty(upload.MimeType) ? "application/octet-stream" : upload.MimeType;
        return new InputMediaUploadedDocument
        {
            flags = 0,
            file = file,
            mime_type = mime,
            attributes = BuildGroupDocumentAttributes(kind, upload, item),
        };
    }

    /// <summary>Собирает DocumentAttribute* для одиночной отправки (из аргументов Bot API).</summary>
    private static DocumentAttribute[] BuildDocumentAttributes(
        MediaType kind, BotUploadFile upload, IReadOnlyDictionary<string, object> args)
    {
        var fileName = string.IsNullOrEmpty(upload.FileName) ? "file" : upload.FileName;
        switch (kind)
        {
            case MediaType.Document:
                return [new DocumentAttributeFilename { file_name = fileName }];
            case MediaType.Audio:
                {
                    var audio = new DocumentAttributeAudio { flags = 0, duration = (int)ReadIntArg(args, "duration", 0) };
                    var title = ReadStringArg(args, "title");
                    var performer = ReadStringArg(args, "performer");
                    if (!string.IsNullOrEmpty(title))
                    {
                        audio.flags |= DocumentAttributeAudio.Flags.has_title;
                        audio.title = title;
                    }

                    if (!string.IsNullOrEmpty(performer))
                    {
                        audio.flags |= DocumentAttributeAudio.Flags.has_performer;
                        audio.performer = performer;
                    }

                    return [audio, new DocumentAttributeFilename { file_name = fileName }];
                }
            case MediaType.Video:
                {
                    DocumentAttributeVideo.Flags flags = 0;
                    if (ReadIntArg(args, "supports_streaming", 1) != 0)
                    {
                        flags |= DocumentAttributeVideo.Flags.supports_streaming;
                    }

                    return
                    [
                        new DocumentAttributeVideo
                        {
                            flags = flags,
                            duration = ReadIntArg(args, "duration", 0),
                            w = (int)ReadIntArg(args, "width", 0),
                            h = (int)ReadIntArg(args, "height", 0),
                        },
                        new DocumentAttributeFilename { file_name = fileName },
                    ];
                }
            case MediaType.Animation:
                return
                [
                    new DocumentAttributeAnimated(),
                    new DocumentAttributeFilename { file_name = fileName },
                ];
            case MediaType.Voice:
                return
                [
                    new DocumentAttributeAudio { flags = DocumentAttributeAudio.Flags.voice, duration = (int)ReadIntArg(args, "duration", 0) },
                    new DocumentAttributeFilename { file_name = fileName },
                ];
            case MediaType.VideoNote:
                {
                    var length = (int)ReadIntArg(args, "length", 0);
                    return
                    [
                        new DocumentAttributeVideo { flags = DocumentAttributeVideo.Flags.round_message, duration = ReadIntArg(args, "duration", 0), w = length, h = length },
                        new DocumentAttributeFilename { file_name = fileName },
                    ];
                }
            case MediaType.Sticker:
                return
                [
                    new DocumentAttributeSticker { flags = 0, alt = ReadStringArg(args, "emoji") ?? "", stickerset = null },
                    new DocumentAttributeFilename { file_name = fileName },
                ];
            default:
                return [new DocumentAttributeFilename { file_name = fileName }];
        }
    }

    /// <summary>Собирает DocumentAttribute* для элемента альбома (из JSON-полей элемента).</summary>
    private static DocumentAttribute[] BuildGroupDocumentAttributes(
        MediaType kind, BotUploadFile upload, JsonElement item)
    {
        var fileName = string.IsNullOrEmpty(upload.FileName) ? "file" : upload.FileName;
        switch (kind)
        {
            case MediaType.Video:
                {
                    DocumentAttributeVideo.Flags flags = 0;
                    if (!GetJsonFalse(item, "supports_streaming"))
                    {
                        flags |= DocumentAttributeVideo.Flags.supports_streaming;
                    }

                    return
                    [
                        new DocumentAttributeVideo
                        {
                            flags = flags,
                            duration = GetJsonLong(item, "duration"),
                            w = (int)GetJsonLong(item, "width"),
                            h = (int)GetJsonLong(item, "height"),
                        },
                        new DocumentAttributeFilename { file_name = fileName },
                    ];
                }
            case MediaType.Audio:
                {
                    var audio = new DocumentAttributeAudio { flags = 0, duration = (int)GetJsonLong(item, "duration") };
                    var title = GetJsonString(item, "title");
                    var performer = GetJsonString(item, "performer");
                    if (!string.IsNullOrEmpty(title))
                    {
                        audio.flags |= DocumentAttributeAudio.Flags.has_title;
                        audio.title = title;
                    }

                    if (!string.IsNullOrEmpty(performer))
                    {
                        audio.flags |= DocumentAttributeAudio.Flags.has_performer;
                        audio.performer = performer;
                    }

                    return [audio, new DocumentAttributeFilename { file_name = fileName }];
                }
            default:
                return [new DocumentAttributeFilename { file_name = fileName }];
        }
    }

    /// <summary>Загружает файл и строит InputFile / InputFileBig (порог 10 МиБ).</summary>
    private static async Task<InputFileBase> UploadMediaAsync(MtProtoClient client, BotUploadFile upload, CancellationToken ct)
    {
        if (upload.Stream.CanSeek)
        {
            // Один и тот же attach:// может использоваться несколькими элементами запроса:
            // поток уже вычитан предыдущей загрузкой, без перемотки Telegram вернёт FILE_PART_EMPTY.
            upload.Stream.Position = 0;
        }

        var fileId = NextRandomId();
        var parts = await client.UploadFileAsync(fileId, upload.Stream, ct).ConfigureAwait(false);
        var name = string.IsNullOrEmpty(upload.FileName) ? "file" : upload.FileName;
        return upload.Stream.Length > AppConstants.MtProto.BigFileThreshold
            ? new InputFileBig { id = fileId, parts = parts, name = name }
            : new InputFile { id = fileId, parts = parts, name = name, md5_checksum = "" };
    }

    private static long NextRandomId() => BitConverter.ToInt64(MtProtoCrypto.RandomBytes(8), 0);
}
