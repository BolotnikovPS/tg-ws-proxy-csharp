#nullable disable
using System.Text;
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>getFile, кэш скачанных файлов и кодирование/декодирование file_id.</summary>
public sealed partial class BotGateway
{
    /// <summary>
    /// getFile: скачивает файл по <c>file_id</c> фрагментами (512 КиБ) через upload.getFile
    /// и кладёт целиком в in-memory кэш; возвращает Bot API File-объект с неотгадываемым file_path.
    /// </summary>
    private async Task<BotApiResult> GetFileAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var fileId = ReadStringArg(args, "file_id");
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return BotApiResult.Fail(400, "file_id is required for getFile");
        }

        // Маршрутизация по первому байту file_id: 2 = фото, иначе документ.
        // Сами дешифраторы повторно валидируют тип/длину и бросают BotGatewayException(400).
        var prefix = DecodeFileIdBytes(fileId);
        InputFileLocationBase location;
        if (prefix.Length > 0 && prefix[0] is 2 or 3)
        {
            var (photo, thumbSize) = DecodePhotoLocation(fileId);
            location = new InputPhotoFileLocation
            {
                id = photo.id,
                access_hash = photo.access_hash,
                file_reference = photo.file_reference,
                // thumb_size = type конкретного PhotoSize: с пустой строкой Telegram отвечает FILE_REFERENCE_EXPIRED.
                // Для file_id старого формата (type=2, без thumb_size) type неизвестен — просим "x"
                // (самый крупный размер обычного фото); несуществующий type Telegram трактует
                // как «отдай самый большой доступный», поэтому скачивание не ломается.
                thumb_size = string.IsNullOrEmpty(thumbSize) ? LegacyPhotoThumbSize : thumbSize,
            };
        }
        else
        {
            var document = DecodeInputDocument(fileId);
            location = new InputDocumentFileLocation
            {
                id = document.id,
                access_hash = document.access_hash,
                file_reference = document.file_reference,
                thumb_size = "", // пустая строка — оригинал, не миниатюру
            };
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        var cap = Math.Max(config.MaxFileCacheBytes, 1L);
        var chunks = new List<byte[]>();
        long total = 0;
        var offset = 0L;

        while (true)
        {
            var part = await client.GetFileAsync(location, offset, FileChunkBytes, ct).ConfigureAwait(false);
            if (part is Upload_FileCdnRedirect)
            {
                // ⚠️ CDN-файл: нужен переход на второй DC и расшифровка (encryption_key/iv + file_hashes);
                // в этом раунде не реализовано — требуется ручная интеграция.
                return BotApiResult.Fail(502, "CDN file download is not supported yet");
            }

            if (part is not Upload_File filePart || filePart.bytes is not { Length: > 0 })
            {
                break; // неизвестный/пустой блок — считаем концом файла
            }

            chunks.Add(filePart.bytes);
            total += filePart.bytes.Length;
            if (total > cap || total > int.MaxValue)
            {
                return BotApiResult.Fail(413, "file too large to cache");
            }

            offset += filePart.bytes.Length;
            if (filePart.bytes.Length < FileChunkBytes)
            {
                break; // последний фрагмент
            }
        }

        var bytes = new byte[(int)total];
        var written = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, bytes, written, chunk.Length);
            written += chunk.Length;
        }

        var filePath = NewFileToken();
        var fileUniqueId = NewFileToken();
        StoreFile(filePath, bytes, contentType: null);

        return BotApiResult.Success(new Dictionary<string, object>
        {
            ["file_id"] = fileId,
            ["file_unique_id"] = fileUniqueId,
            ["file_size"] = bytes.Length,
            ["file_path"] = filePath,
        });
    }

    /// <summary>Случайный неотгадываемый маркер файла: base64url(RandomBytes(32)) без padding.</summary>
    private static string NewFileToken()
        => Convert.ToBase64String(MtProtoCrypto.RandomBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Кладёт байты в in-memory кэш и при необходимости вытесняет старейшие по LastAccessTicks (LRU).</summary>
    internal void StoreFile(string filePath, byte[] data, string contentType)
    {
        _fileCache[filePath] = new FileCacheEntry(data, contentType);
        Interlocked.Add(ref _fileCacheBytes, data.Length);
        EvictIfNeeded();
    }

    /// <summary>
    /// Попытка отдать скачанный файл по <c>file_path</c>. При попадании обновляет момент доступа (LRU).
    /// Возвращает false и пустые out-параметры при промахе.
    /// </summary>
#nullable enable
    public bool TryGetFile(string filePath, out ReadOnlyMemory<byte> data, out string? contentType)
    {
        if (_fileCache.TryGetValue(filePath, out var entry))
        {
            entry.LastAccessTicks = DateTime.UtcNow.Ticks;
            data = entry.Data;
            contentType = entry.ContentType;
            return true;
        }

        data = default;
        contentType = null;
        return false;
    }

#nullable disable

    /// <summary>LRU-вытеснение: пока суммарный размер превышает лимит, удаляет записи с самой старой LastAccessTicks.</summary>
    private void EvictIfNeeded()
    {
        var cap = Math.Max(config.MaxFileCacheBytes, 1L);
        while (Interlocked.Read(ref _fileCacheBytes) > cap)
        {
            string victimKey = null;
            var oldest = long.MaxValue;
            foreach (var kv in _fileCache)
            {
                if (kv.Value.LastAccessTicks < oldest)
                {
                    oldest = kv.Value.LastAccessTicks;
                    victimKey = kv.Key;
                }
            }

            if (victimKey == null || !_fileCache.TryRemove(victimKey, out var victim))
            {
                break;
            }

            Interlocked.Add(ref _fileCacheBytes, -victim.Data.Length);
        }
    }

    /// <summary>Запись in-memory кэша скачанного файла: байты, Content-Type (может быть null) и момент доступа.</summary>
    private sealed class FileCacheEntry(byte[] data, string contentType)
    {
        public byte[] Data { get; } = data;
        public string ContentType { get; } = contentType;
        public long LastAccessTicks { get; set; } = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Собирает Bot API file_id фото (обратная к <see cref="DecodePhotoLocation"/> раскладка):
    /// [0]=type(3), [1:5]=dc_id(int32), [5:13]=id(int64), [13:21]=access_hash(int64),
    /// [21]=длина thumb_size(1 байт), [22:22+n]=thumb_size, [22+n:]=file_reference.
    /// <para>
    /// thumb_size обязателен: это <c>type</c> конкретного <see cref="PhotoSize"/>. Без него Telegram
    /// отвечает <c>FILE_REFERENCE_EXPIRED</c> на upload.getFile — file_reference привязан к точной локации.
    /// </para>
    /// </summary>
    internal static string EncodeInputPhoto(long id, long accessHash, int dcId, byte[] fileReference, string thumbSize)
    {
        var fr = fileReference ?? [];
        var thumb = Encoding.UTF8.GetBytes(thumbSize ?? string.Empty);
        if (thumb.Length > 255)
        {
            thumb = thumb[..255];
        }

        var payload = new byte[22 + thumb.Length + fr.Length];
        payload[0] = 3; // photo с явным thumb_size
        BitConverter.GetBytes(dcId).CopyTo(payload, 1);
        BitConverter.GetBytes(id).CopyTo(payload, 5);
        BitConverter.GetBytes(accessHash).CopyTo(payload, 13);
        payload[21] = (byte)thumb.Length;
        thumb.CopyTo(payload, 22);
        fr.CopyTo(payload, 22 + thumb.Length);
        return ToBase64Url(payload);
    }

    /// <summary>
    /// Собирает Bot API file_id документа (обратная к <see cref="DecodeInputDocument"/> раскладка):
    /// [0]=type(5), [1]=subtype(0), [2:6]=dc_id(int32), [6:14]=id(int64), [14:22]=access_hash(int64), [22:]=file_reference.
    /// </summary>
    internal static string EncodeInputDocument(long id, long accessHash, int dcId, byte[] fileReference)
    {
        var fr = fileReference ?? [];
        var payload = new byte[22 + fr.Length];
        payload[0] = 5; // document
        payload[1] = 0; // generic document subtype
        BitConverter.GetBytes(dcId).CopyTo(payload, 2);
        BitConverter.GetBytes(id).CopyTo(payload, 6);
        BitConverter.GetBytes(accessHash).CopyTo(payload, 14);
        fr.CopyTo(payload, 22);
        return ToBase64Url(payload);
    }

    /// <summary>Стабильный уникальный идентификатор файла для Bot API (не декодируется обратно).</summary>
    internal static string BuildFileUniqueId(byte kind, long id)
    {
        var raw = new byte[9];
        raw[0] = kind;
        BitConverter.GetBytes(id).CopyTo(raw, 1);
        return ToBase64Url(raw);
    }

    private static string ToBase64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// ⚠️ Расшифровка file_id фото в InputPhoto + thumb_size. Раскладка (Bot API, little-endian):
    /// [0]=type(3), [1:5]=dc_id(int32), [5:13]=id(uint64), [13:21]=access_hash(uint64),
    /// [21]=длина thumb_size, [22:22+n]=thumb_size, [22+n:]=file_reference.
    /// Устаревшая раскладка (type=2, без thumb_size) тоже читается — на случай file_id, выданных ранее.
    /// </summary>
    internal static (InputPhoto Photo, string ThumbSize) DecodePhotoLocation(string fileId)
    {
        var data = DecodeFileIdBytes(fileId);

        if (data.Length >= 22 && data[0] == 3)
        {
            var thumbLength = data[21];
            if (data.Length < 22 + thumbLength)
            {
                throw new BotGatewayException(400, "invalid photo file_id");
            }

            return (new InputPhoto
            {
                id = BitConverter.ToInt64(data, 5),
                access_hash = BitConverter.ToInt64(data, 13),
                file_reference = data[(22 + thumbLength)..],
            }, Encoding.UTF8.GetString(data, 22, thumbLength));
        }

        if (data.Length < 21 || data[0] != 2)
        {
            throw new BotGatewayException(400, "invalid photo file_id");
        }

        return (new InputPhoto
        {
            id = BitConverter.ToInt64(data, 5),
            access_hash = BitConverter.ToInt64(data, 13),
            file_reference = data[21..],
        }, string.Empty);
    }

    /// <summary>InputPhoto из Bot API file_id (thumb_size отбрасывается — он нужен только для скачивания).</summary>
    internal static InputPhoto DecodeInputPhoto(string fileId) => DecodePhotoLocation(fileId).Photo;

    /// <summary>
    /// ⚠️ Расшифровка file_id документа в InputDocument. Формат (Bot API, little-endian):
    /// [0]=type(5|8), [1]=subtype, [2:6]=dc_id(int32), [6:14]=id(uint64), [14:22]=access_hash(uint64), [22:]=file_reference.
    /// Смещения сверены с общепринятой раскладкой Bot API file_id, но требуют ручной проверки на реальных ID.
    /// </summary>
    internal static InputDocument DecodeInputDocument(string fileId)
    {
        var data = DecodeFileIdBytes(fileId);
        if (data.Length < 22 || data[0] != 5 && data[0] != 8)
        {
            throw new BotGatewayException(400, "invalid document file_id");
        }

        return new InputDocument
        {
            id = BitConverter.ToInt64(data, 6),
            access_hash = BitConverter.ToInt64(data, 14),
            file_reference = data[22..],
        };
    }

    private static byte[] DecodeFileIdBytes(string fileId)
    {
        var normalized = fileId.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            throw new BotGatewayException(400, "invalid file_id");
        }
    }
}
