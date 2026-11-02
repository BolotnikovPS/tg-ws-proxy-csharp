#nullable disable
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// MTProto client. Handles connection, auth key exchange, and RPC calls.
/// Uses only our own crypto and transport - no WTelegramClient dependency.
/// </summary>
public class MtProtoClient(string host, int port, byte[] secret = null) : IDisposable
{
    private readonly string _host = host;
    private readonly int _port = port;
    private readonly byte[] _secret = secret;
    private MtProtoTransport _transport;
    private byte[] _authKey;
    private long _authKeyId;
    private long _salt;
    private long _sessionId = BitConverter.ToInt64(MtProtoCrypto.RandomBytes(8), 0);
    private long _lastSentMsgId;
    private int _dcId;
    private bool _testMode;
    private long _serverTimeOffset;

    // Состояние ленты обновлений (pts/qts/date/seq) — заполняется после updates.getState/getDifference,
    // переносится в BotSession и обратно для инкрементальных getUpdates без потери истории.
    private long _updatePts;
    private long _updateQts;
    private long _updateSeq;
    private DateTime _updateDate;

    // Telegram DC addresses
    public static readonly (string host, int port)[] ProductionDcs =
    [
        ("149.154.175.50", 443),   // DC1
        ("149.154.167.50", 443),   // DC2 (default)
        ("149.154.175.100", 443),  // DC3
        ("149.154.167.220", 443),  // DC4
        ("149.154.167.91", 443),    // DC5
    ];

    public static readonly (string host, int port)[] TestDcs =
    [
        ("149.154.167.40", 443),   // Test DC2 (default)
    ];

    public async Task ConnectAsync(bool testMode = false, CancellationToken ct = default)
        => await ConnectAsync(null, testMode, ct);

    /// <summary>
    /// Подключается и, если передан валидный <paramref name="session"/>, восстанавливает его состояние —
    /// обмен auth key пропускается. Иначе выполняет полный обмен auth key.
    /// </summary>
    public async Task ConnectAsync(BotSession session, bool testMode = false, CancellationToken ct = default)
    {
        _testMode = testMode;
        _dcId = 2; // оба дефолтных адреса (ProductionDcs[1] и TestDcs[0]) — это DC2

        // MTProxy-режим: секрет задан — подключаемся к заданному хосту/порту (свой обход,
        // 64-байтная обфускация). Прямой режим: берём адрес DC из встроенной таблицы.
        var (host, port) = _secret != null ? (_host, _port) : (testMode ? TestDcs[0] : ProductionDcs[1]);
        _transport = new MtProtoTransport(host, port, _secret);
        await _transport.ConnectAsync(ct);

        if (session?.IsValid == true)
        {
            ImportSession(session);
            return;
        }

        // Auth key exchange
        await CreateAuthKeyAsync(ct);
    }

    private async Task CreateAuthKeyAsync(CancellationToken ct)
    {
        var sha256 = SHA256.Create();

        // 1) ReqPqMulti
        var nonce = new Int128(MtProtoCrypto.RandomBytes(16));
        var resPQ = await SendUnencryptedAsync<ResPQ>(new TgWsProxy.Infrastructure.MtProto.TL.Methods.ReqPqMulti { nonce = nonce }, ct);

        if (resPQ.nonce != nonce)
        {
            throw new InvalidOperationException("Nonce mismatch");
        }

        // 2) Select public key
        var fingerprint = resPQ.server_public_key_fingerprints.FirstOrDefault(MtProtoCrypto.HasPublicKey);
        if (fingerprint == 0)
        {
            throw new InvalidOperationException("Couldn't match any server_public_key_fingerprints");
        }

        var (nBytes, eBytes) = MtProtoCrypto.GetPublicKey(fingerprint);

        // 3) Factorize PQ
        var (p, q) = MtProtoCrypto.FactorizePQ(resPQ.pq);

        // 4) PQInnerDataDc
        var newNonce = new Int256(MtProtoCrypto.RandomBytes(32));
        var dcId = 0; // main DC
        var pqInnerData = new PQInnerDataDc
        {
            pq = resPQ.pq,
            p = p,
            q = q,
            nonce = nonce,
            server_nonce = resPQ.server_nonce,
            new_nonce = newNonce,
            dc = dcId
        };

        // 4.1) RSA_PAD encrypt
        var encryptedData = RsaPadEncrypt(pqInnerData, nBytes, eBytes, sha256);

        // Send ReqDHParams
        var serverDHparams = await SendUnencryptedAsync<ServerDHParams>(
            new TgWsProxy.Infrastructure.MtProto.TL.Methods.ReqDHParams
            {
                nonce = nonce,
                server_nonce = resPQ.server_nonce,
                p = p,
                q = q,
                public_key_fingerprint = fingerprint,
                encrypted_data = encryptedData
            }, ct);

        if (serverDHparams is not ServerDHParamsOk dhParamsOk)
        {
            throw new InvalidOperationException("not server_DH_params_ok");
        }

        if (dhParamsOk.nonce != nonce)
        {
            throw new InvalidOperationException("Nonce mismatch");
        }

        if (dhParamsOk.server_nonce != resPQ.server_nonce)
        {
            throw new InvalidOperationException("Server Nonce mismatch");
        }

        // 5) Decrypt server DH params
        var (tmpAesKey, tmpAesIv) = MtProtoCrypto.ConstructTmpAESKeyIV(
            resPQ.server_nonce, newNonce);

        var answer = MtProtoCrypto.DecryptServerDHParams(dhParamsOk.encrypted_answer, tmpAesKey, tmpAesIv);
        using var answerReader = new BinaryReader(new MemoryStream(answer));
        var answerHash = answerReader.ReadBytes(20);
        var answerObj = answerReader.ReadTLObject();
        if (answerObj is not ServerDHInnerData serverDHinnerData)
        {
            throw new InvalidOperationException("not server_DH_inner_data");
        }

        if (serverDHinnerData.nonce != nonce)
        {
            throw new InvalidOperationException("Nonce mismatch");
        }

        if (serverDHinnerData.server_nonce != resPQ.server_nonce)
        {
            throw new InvalidOperationException("Server Nonce mismatch");
        }

        var g = serverDHinnerData.g;
        var gA = MtProtoCrypto.BigEndianToBigInteger(serverDHinnerData.g_a);
        var dhPrime = MtProtoCrypto.BigEndianToBigInteger(serverDHinnerData.dh_prime);

        MtProtoCrypto.CheckGoodPrime(dhPrime);
        MtProtoCrypto.CheckGoodGaAndGb(gA, dhPrime);

        // 6) Client DH inner data
        var bBytes = MtProtoCrypto.RandomBytes(256);
        var b = MtProtoCrypto.BigEndianToBigInteger(bBytes);
        var gB = BigInteger.ModPow(g, b, dhPrime);

        MtProtoCrypto.CheckGoodGaAndGb(gB, dhPrime);

        var clientDHinnerData = new ClientDHInnerData
        {
            nonce = nonce,
            server_nonce = resPQ.server_nonce,
            retry_id = 0,
            g_b = MtProtoCrypto.BigIntegerToBigEndian(gB, 256)
        };

        // Encrypt client DH inner data
        var clearStream = new MemoryStream(384)
        {
            Position = 20
        };
        using var clearWriter = new BinaryWriter(clearStream);
        clearWriter.WriteTLObject(clientDHinnerData);
        var clearLength = (int)clearStream.Length;
        var paddingToAdd = (0x7FFFFFF0 - clearLength) % 16;
        clearStream.SetLength(clearLength + paddingToAdd);
        var clearBuffer = clearStream.GetBuffer();
        MtProtoCrypto.RandomBytes(0).CopyTo(clearBuffer, clearLength); // placeholder
        new Random().NextBytes(clearBuffer.AsSpan(clearLength, paddingToAdd));
        SHA1.HashData(clearBuffer.AsSpan(20, clearLength - 20)).CopyTo(clearBuffer, 0);

        var clientEncrypted = MtProtoCrypto.EncryptClientDHInnerData(
            clearBuffer.AsSpan(0, clearLength + paddingToAdd).ToArray(), tmpAesKey, tmpAesIv);

        var setClientDHparamsAnswer = await SendUnencryptedAsync<SetClientDHParamsAnswer>(
            new TgWsProxy.Infrastructure.MtProto.TL.Methods.SetClientDHParams
            {
                nonce = nonce,
                server_nonce = resPQ.server_nonce,
                encrypted_data = clientEncrypted
            }, ct);

        // 7) Compute auth key
        var authKey = MtProtoCrypto.ComputeAuthKey(gA, bBytes, dhPrime);

        // 8) Verify
        if (setClientDHparamsAnswer is not DhGenOk dhGenOk)
        {
            throw new InvalidOperationException("not dh_gen_ok");
        }

        if (dhGenOk.nonce != nonce)
        {
            throw new InvalidOperationException("Nonce mismatch");
        }

        if (dhGenOk.server_nonce != resPQ.server_nonce)
        {
            throw new InvalidOperationException("Server Nonce mismatch");
        }

        // Compute auth key id
        var authKeyHash = MtProtoCrypto.Sha1(authKey);
        _authKeyId = BitConverter.ToInt64(authKeyHash, 12);
        _authKey = authKey;
        _salt = BitConverter.ToInt64(newNonce, 0) ^
                BitConverter.ToInt64(resPQ.server_nonce, 0);
        sha256.Dispose();
    }

    private static byte[] RsaPadEncrypt(PQInnerDataDc data, byte[] nBytes, byte[] eBytes, SHA256 sha256)
    {
        var n = MtProtoCrypto.BigEndianToBigInteger(nBytes);
        byte[] encryptedData = null;
        do
        {
            var clearStream = new MemoryStream(256);
            var writer = new BinaryWriter(clearStream);
            var aesKey = MtProtoCrypto.RandomBytes(32);
            var zeroIv = new byte[32];
            clearStream.Write(aesKey, 0, 32);
            writer.WriteTLObject(data);
            var clearLength = (int)clearStream.Position - 32;
            if (clearLength > 144)
            {
                throw new InvalidOperationException("PQInnerData too big");
            }

            var clearBuffer = clearStream.GetBuffer();
            MtProtoCrypto.RandomBytes(0).CopyTo(clearBuffer, 32 + clearLength); // placeholder
            new Random().NextBytes(clearBuffer.AsSpan(32 + clearLength, 192 - clearLength));
            sha256.ComputeHash(clearBuffer, 0, 32 + 192).CopyTo(clearBuffer, 224);
            Array.Reverse(clearBuffer, 32, 192);
            var aesEncrypted = MtProtoCrypto.AesIgeEncryptDecrypt(
                clearBuffer.AsSpan(32, 224).ToArray(), aesKey, zeroIv, true);
            var hashAes = sha256.ComputeHash(aesEncrypted);
            for (var i = 0; i < 32; i++)
            {
                clearBuffer[i] = (byte)(aesKey[i] ^ hashAes[i]);
            }

            aesEncrypted.CopyTo(clearBuffer, 32);
            var x = MtProtoCrypto.BigEndianToBigInteger(clearBuffer);
            if (x < n)
            {
                encryptedData = BigInteger.ModPow(x, MtProtoCrypto.BigEndianToBigInteger(eBytes), n)
                    .ToByteArray(isUnsigned: true, isBigEndian: true);
                if (encryptedData.Length < 256)
                {
                    var padded = new byte[256];
                    Array.Copy(encryptedData, 0, padded, 256 - encryptedData.Length, encryptedData.Length);
                    encryptedData = padded;
                }
            }
        } while (encryptedData == null);
        return encryptedData;
    }

    private async Task<T> SendUnencryptedAsync<T>(IObject msg, CancellationToken ct) where T : IObject
    {
        using var memStream = new MemoryStream(1024);
        using var writer = new BinaryWriter(memStream);
        writer.Write(0); // payload_len placeholder
        writer.Write(0L); // auth_key_id = 0 (unencrypted)
        var msgId = GenerateMsgId();
        writer.Write(msgId); // message_id
        writer.Write(0); // message_data_length placeholder
        writer.WriteTLObject(msg);
        var dataLen = (int)memStream.Length - 24;
        BinaryPrimitives.WriteInt32LittleEndian(memStream.GetBuffer().AsSpan(20), dataLen);

        var payload = memStream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), payload.Length - 4);

        await _transport.SendAsync(payload, ct);

        // Receive response (framed as [payload_len][auth_key_id][msg_id][data_len][data])
        var response = await _transport.ReceiveAsync(ct);
        using var reader = new BinaryReader(new MemoryStream(response));
        reader.ReadInt32(); // skip payload_len
        var respAuthKeyId = reader.ReadInt64();
        var respMsgId = reader.ReadInt64();
        var respDataLen = reader.ReadInt32();
        var obj = reader.ReadTLObject();
        return (T)obj;
    }

    internal long GenerateMsgId()
    {
        // msg_id = unixTime * 2^32, must be a multiple of 4.
        // Formula from reference implementation (no float overflow, stays in long range):
        // _serverTimeOffset (seconds) = server clock − local clock, applied to the timestamp.
        var ticks = DateTime.UtcNow.Ticks - 621355968000000000L
                    + _serverTimeOffset * TimeSpan.TicksPerSecond; // ticks since Unix epoch (server-adjusted)
        var msgId = ticks * 428 + (ticks >> 24) * 25110956;
        if (msgId <= _lastSentMsgId)
        {
            msgId = _lastSentMsgId + 4;
        }

        _lastSentMsgId = msgId;
        return msgId;
    }

    private async Task<T> SendEncryptedAsync<T>(IObject msg, CancellationToken ct) where T : IObject
    {
        // bad_server_salt triggers a transparent resend with the freshly-set salt (bounded attempts).
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var sha256 = SHA256.Create();
            using var rng = RandomNumberGenerator.Create();

            // New msg id and seqno
            var msgId = GenerateMsgId();
            // content-related messages MUST use even seq_no, starting at 0 (ping uses odd).
            var seqno = _seqnoCounter++ * 2;

            // Build clear text
            using var clearStream = new MemoryStream(1024);
            using var clearWriter = new BinaryWriter(clearStream);
            clearWriter.Write(_authKey, 88, 32); // auth_key[88..120]
            clearWriter.Write(_salt);
            clearWriter.Write(_sessionId);
            clearWriter.Write(msgId);
            clearWriter.Write(seqno);
            clearWriter.Write(0); // message_data_length placeholder
            clearWriter.WriteTLObject(msg);
            var clearLength = (int)clearStream.Length - 32; // 32 = auth_key_part (32 bytes)
            var padding = (0x7FFFFFF0 - (32 + clearLength)) % 16;
            padding += RandomNumberGenerator.GetInt32(2, 16) * 16;
            if (padding < 12)
            {
                padding += 16;
            }

            clearStream.SetLength(32 + clearLength + padding);
            var clearBuffer = clearStream.GetBuffer();
            BinaryPrimitives.WriteInt32LittleEndian(clearBuffer.AsSpan(60), clearLength); // patch message_data_length
            rng.GetBytes(clearBuffer.AsSpan(32 + clearLength, padding));

            // Compute msg_key
            var msgKeyLarge = sha256.ComputeHash(clearBuffer, 0, 32 + clearLength + padding);
            var msgKey = new byte[16];
            Array.Copy(msgKeyLarge, 8, msgKey, 0, 16);

            // Derive AES key/iv
            var (aesKey, aesIv) = MtProtoCrypto.DeriveAesKeyIv(msgKey, 0, _authKey, 0, sha256);

            // Encrypt
            var encryptedData = MtProtoCrypto.AesIgeEncryptDecrypt(
                clearBuffer.AsSpan(32, clearLength + padding).ToArray(), aesKey, aesIv, true);

            // Build payload
            using var memStream = new MemoryStream(1024);
            using var writer = new BinaryWriter(memStream);
            writer.Write(0); // payload_len placeholder
            writer.Write(_authKeyId);
            writer.Write(msgKey);
            writer.Write(encryptedData);
            var payload = memStream.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), payload.Length - 4);

            await _transport.SendAsync(payload, ct);

            // Receive response
            var response = await _transport.ReceiveAsync(ct);
            using var respReader = new BinaryReader(new MemoryStream(response));
            _ = respReader.ReadInt32(); // payload_len
            _ = respReader.ReadInt64(); // auth_key_id
            var respMsgKey = respReader.ReadBytes(16);
            var respEncrypted = respReader.ReadBytes(response.Length - 4 - 8 - 16);

            // Derive AES key/iv for decryption
            var (decAesKey, decAesIv) = MtProtoCrypto.DeriveAesKeyIv(respMsgKey, 0, _authKey, 0, sha256);

            // Decrypt
            var decrypted = MtProtoCrypto.AesIgeEncryptDecrypt(respEncrypted, decAesKey, decAesIv, false);

            try
            {
                var result = ReadEncryptedResponse(decrypted, msgId)
                    ?? throw new InvalidOperationException($"Empty response to msg_id {msgId}");
                return (T)result;
            }
            catch (ServerSaltChangedException) when (attempt < 3)
            {
                // _salt has been refreshed inside ReadEncryptedResponse; resend with the new salt.
            }
        }

        throw new InvalidOperationException("Persistent bad_server_salt across retries");
    }

    /// <summary>
    /// Parses a decrypted inbound RPC payload and unwraps the result for <paramref name="expectedMsgId"/>.
    /// Handles msg_container → new_session_created / rpc_result / bad_server_salt / bad_msg_notification,
    /// bare rpc_result / new_session_created, gzip_packed, and rpc_error (thrown as <see cref="RpcException"/>).
    /// </summary>
    internal IObject ReadEncryptedResponse(byte[] decrypted, long expectedMsgId)
    {
        using var reader = new BinaryReader(new MemoryStream(decrypted));
        reader.ReadBytes(32); // auth_key[88..120]
        _ = reader.ReadInt64(); // server_salt (transport copy; authoritative one may arrive via new_session_created)
        _ = reader.ReadInt64(); // session_id
        _ = reader.ReadInt64(); // msg_id
        _ = reader.ReadInt32(); // seq_no
        _ = reader.ReadInt32(); // message_data_length
        return ProcessServerObject(reader.ReadTLObject(), expectedMsgId);
    }

    internal IObject ProcessServerObject(IObject obj, long expectedMsgId)
    {
        if (obj is MsgContainer container)
        {
            IObject result = null;
            foreach (var message in container.messages)
            {
                switch (message.body)
                {
                    case NewSessionCreated nsc:
                        _salt = nsc.server_salt;
                        break;
                    case BadServerSalt badSalt:
                        _salt = badSalt.new_server_salt;
                        throw new ServerSaltChangedException(badSalt.new_server_salt);
                    case BadMsgNotification badMsg:
                        throw new InvalidOperationException(
                            $"bad_msg_notification: error_code={badMsg.error_code}, bad_msg_id={badMsg.bad_msg_id}, bad_msg_seqno={badMsg.bad_msg_seqno}");
                    case RpcResult rpc when rpc.req_msg_id == expectedMsgId:
                        result = UnwrapResult(rpc.result);
                        break;
                    case RpcResult:
                        break; // result for another req_msg_id — ignore and keep scanning
                }
            }
            return result;
        }

        if (obj is RpcResult rpcResult)
        {
            if (rpcResult.req_msg_id != expectedMsgId)
            {
                throw new InvalidOperationException($"Unexpected rpc_result req_msg_id {rpcResult.req_msg_id}, expected {expectedMsgId}");
            }

            return UnwrapResult(rpcResult.result);
        }

        if (obj is NewSessionCreated nsc2)
        {
            _salt = nsc2.server_salt;
            return null; // no RPC result in this payload
        }

        if (obj is BadServerSalt badSalt2)
        {
            _salt = badSalt2.new_server_salt;
            throw new ServerSaltChangedException(badSalt2.new_server_salt);
        }

        if (obj is BadMsgNotification badMsg2)
        {
            throw new InvalidOperationException($"bad_msg_notification: error_code={badMsg2.error_code}");
        }

        // Some responses arrive unwrapped (bare object).
        return obj;
    }

    private static IObject UnwrapResult(object result)
    {
        if (result is RpcError err)
        {
            throw new RpcException(err.error_code, err.error_message);
        }

        if (result is not GzipPacked gz)
        {
            return (IObject)result;
        }

        using var gzMs = new MemoryStream(gz.packed_data);
        using var gzStream = new GZipStream(gzMs, CompressionMode.Decompress);
        using var gzReader = new BinaryReader(gzStream);
        var inner = gzReader.ReadTLObject();
        if (inner is RpcError gzErr)
        {
            throw new RpcException(gzErr.error_code, gzErr.error_message);
        }

        return inner;
    }

    internal sealed class ServerSaltChangedException(long newServerSalt) : Exception
    {
        public readonly long NewServerSalt = newServerSalt;
    }

    private int _seqnoCounter;

    public async Task<T> InvokeAsync<T>(IObject method, CancellationToken ct = default) where T : IObject
    {
        if (_authKey == null)
        {
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        }

        return await SendEncryptedAsync<T>(method, ct);
    }

    /// <summary>
    /// Вызывает метод с результатом bool (boolTrue#997275b5 / boolFalse#bc799737),
    /// например upload.saveFilePart, messages.setTyping, bots.setBotCommands.
    /// </summary>
    public async Task<bool> InvokeBoolAsync(IObject method, CancellationToken ct = default)
    {
        var result = await SendEncryptedAsync<BoolValue>(method, ct).ConfigureAwait(false);
        return result?.Value ?? false;
    }

    /// <summary>Максимальный размер части upload.saveFilePart — 512 КиБ (ограничение Telegram).</summary>
    public const int MaxFilePartSize = 512 * 1024;

    /// <summary>Файлы крупнее 10 МиБ загружаются через upload.saveBigFilePart (InputFileBig).</summary>
    public const long BigFileThreshold = 10L * 1024 * 1024;

    /// <summary>
    /// Загружает файл в Telegram по частям (upload.saveFilePart / upload.saveBigFilePart).
    /// Возвращает общее число частей; вызывающий сам выбирает InputFile vs InputFileBig.
    /// </summary>
    public async Task<int> UploadFileAsync(long fileId, Stream source, CancellationToken ct = default)
    {
        if (!source.CanSeek)
        {
            throw new ArgumentException("Upload source must be seekable", nameof(source));
        }

        var total = source.Length;
        var big = total > BigFileThreshold;
        var totalParts = (int)((total + MaxFilePartSize - 1) / MaxFilePartSize);
        if (totalParts == 0)
        {
            totalParts = 1; // пустой файл — одна пустая часть
        }

        var buffer = new byte[MaxFilePartSize];
        for (var part = 0; part < totalParts; part++)
        {
            var chunk = await ReadFilePartAsync(source, buffer, ct).ConfigureAwait(false);
            if (big)
            {
                await InvokeBoolAsync(new TgWsProxy.Infrastructure.MtProto.TL.Methods.Upload_SaveBigFilePart
                {
                    file_id = fileId,
                    file_part = part,
                    file_total_parts = totalParts,
                    bytes = chunk,
                }, ct).ConfigureAwait(false);
            }
            else
            {
                await InvokeBoolAsync(new TgWsProxy.Infrastructure.MtProto.TL.Methods.Upload_SaveFilePart
                {
                    file_id = fileId,
                    file_part = part,
                    bytes = chunk,
                }, ct).ConfigureAwait(false);
            }
        }

        return totalParts;
    }

    /// <summary>
    /// upload.getFile#be5335be → upload.File. Скачивает файловый фрагмент по offset/limit
    /// (маршрут /file/bot&lt;token&gt;/&lt;file_path&gt; строится в Web-слое).
    /// </summary>
    public Task<TgWsProxy.Infrastructure.MtProto.TL.Upload_FileBase> GetFileAsync(
        TgWsProxy.Infrastructure.MtProto.TL.InputFileLocationBase location, long offset, int limit, CancellationToken ct = default)
        => InvokeAsync<TgWsProxy.Infrastructure.MtProto.TL.Upload_FileBase>(
            new TgWsProxy.Infrastructure.MtProto.TL.Methods.Upload_GetFile
            {
                flags = 0,
                location = location,
                offset = offset,
                limit = limit,
            }, ct);

    private static async Task<byte[]> ReadFilePartAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break; // EOF на последней части
            }

            offset += read;
        }

        var chunk = new byte[offset];
        Array.Copy(buffer, chunk, offset);
        return chunk;
    }

    /// <summary>
    /// Логин бота через auth.importBotAuthorization#67a3ff2c → auth.Authorization.
    /// Требует предварительный <see cref="ConnectAsync(bool, CancellationToken)"/> (обмен auth key).
    /// </summary>
    public Task<TgWsProxy.Infrastructure.MtProto.TL.Auth_AuthorizationBase> ImportBotAuthorizationAsync(
        int apiId, string apiHash, string botToken, CancellationToken ct = default)
        => InvokeAsync<TgWsProxy.Infrastructure.MtProto.TL.Auth_AuthorizationBase>(
            new TgWsProxy.Infrastructure.MtProto.TL.Methods.ImportBotAuthorization
            {
                flags = 0,
                api_id = apiId,
                api_hash = apiHash,
                bot_auth_token = botToken,
            }, ct);

    /// <summary>users.getUsers#d91a548 → Vector&lt;User&gt;. Возвращает пользователей (self-id через InputUserSelf).</summary>
    public Task<TgWsProxy.Infrastructure.MtProto.TL.UserBase[]> GetUsersAsync(
        TgWsProxy.Infrastructure.MtProto.TL.InputUserBase[] id, CancellationToken ct = default)
        => InvokeVectorAsync<TgWsProxy.Infrastructure.MtProto.TL.UserBase>(
            new TgWsProxy.Infrastructure.MtProto.TL.Methods.Users_GetUsers { id = id }, ct);

    /// <summary>messages.sendMessage#fef48f62 → updates.Updates. Отправляет текст в чат через InputPeer.</summary>
    public Task<TgWsProxy.Infrastructure.MtProto.TL.UpdatesBase> SendMessageAsync(
        TgWsProxy.Infrastructure.MtProto.TL.InputPeer peer, string message, CancellationToken ct = default)
        => InvokeAsync<TgWsProxy.Infrastructure.MtProto.TL.UpdatesBase>(
            new TgWsProxy.Infrastructure.MtProto.TL.Methods.Messages_SendMessage
            {
                flags = 0,
                peer = peer,
                message = message,
                random_id = BitConverter.ToInt64(MtProtoCrypto.RandomBytes(8), 0),
            }, ct);

    /// <summary>updates.getState#edd4882a → updates.State (pts/qts/date/seq — начальное состояние ленты обновлений).</summary>
    public Task<TgWsProxy.Infrastructure.MtProto.TL.Updates_State> GetStateAsync(CancellationToken ct = default)
        => InvokeAsync<TgWsProxy.Infrastructure.MtProto.TL.Updates_State>(
            new TgWsProxy.Infrastructure.MtProto.TL.Methods.Updates_GetState(), ct);

    /// <summary>updates.getDifference#19c2f763 → updates.Difference. Получает накопившиеся обновления после pts.</summary>
    public Task<TgWsProxy.Infrastructure.MtProto.TL.Updates_DifferenceBase> GetDifferenceAsync(
        int pts, DateTime date, int qts, CancellationToken ct = default)
        => InvokeAsync<TgWsProxy.Infrastructure.MtProto.TL.Updates_DifferenceBase>(
            new TgWsProxy.Infrastructure.MtProto.TL.Methods.Updates_GetDifference
            {
                flags = TgWsProxy.Infrastructure.MtProto.TL.Methods.Updates_GetDifference.Flags.has_pts_total_limit,
                pts = pts,
                pts_total_limit = 0,
                date = date,
                qts = qts,
            }, ct);

    /// <summary>Invoke метода с результатом TL vector#1cb5c415 (например users.getUsers).</summary>
    public async Task<TElement[]> InvokeVectorAsync<TElement>(IObject method, CancellationToken ct = default)
        where TElement : IObject
    {
        var vector = await SendEncryptedAsync<TLVector>(method, ct);
        if (vector?.Items is not { Length: > 0 } items)
        {
            return [];
        }

        var result = new TElement[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            result[i] = (TElement)items[i];
        }

        return result;
    }

    /// <summary>Применяет результат updates.getState, сохраняя pts/qts/date/seq для следующих getUpdates.</summary>
    public void ApplyUpdatesState(TgWsProxy.Infrastructure.MtProto.TL.Updates_State state)
    {
        if (state == null)
        {
            return;
        }

        _updatePts = state.pts;
        _updateQts = state.qts;
        _updateDate = state.date;
        _updateSeq = state.seq;
    }

    /// <summary>updates.getState + запоминание состояния (первый синхрон перед getUpdates).</summary>
    public async Task<TgWsProxy.Infrastructure.MtProto.TL.Updates_State> SyncUpdateStateAsync(CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct).ConfigureAwait(false);
        ApplyUpdatesState(state);
        return state;
    }

    /// <summary>Извлекает числовой bot id из bot token («&lt;botId&gt;:&lt;secret&gt;») — ключ файла сессии.</summary>
    public static long BotIdFromToken(string botToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        var colon = botToken.IndexOf(':');
        if (colon <= 0 || colon == botToken.Length - 1)
        {
            throw new FormatException($"Invalid bot token: expected '<botId>:<secret>'");
        }

        if (!long.TryParse(botToken.AsSpan(0, colon), out var botId) || botId <= 0)
        {
            throw new FormatException($"Invalid bot id in token '{botToken}'");
        }

        return botId;
    }

    /// <summary>Снимок текущего MTProto-состояния для персистентности по ботам.</summary>
    public BotSession ExportSession() => new()
    {
        AuthKey = _authKey,
        AuthKeyId = _authKeyId,
        Salt = _salt,
        SessionId = _sessionId,
        DcId = _dcId,
        TestMode = _testMode,
        ServerTimeOffset = _serverTimeOffset,
        LastMsgId = _lastSentMsgId,
        SeqNo = _seqnoCounter,
        Pts = _updatePts,
        Qts = _updateQts,
        Seq = _updateSeq,
        Date = _updateDate == default
            ? 0
            : new DateTimeOffset(_updateDate.ToUniversalTime()).ToUnixTimeSeconds(),
    };

    /// <summary>Восстанавливает MTProto-состояние из сессии (используется вместо обмена auth key).</summary>
    public void ImportSession(BotSession session)
    {
        _authKey = session.AuthKey;
        _authKeyId = session.AuthKeyId;
        _salt = session.Salt;
        _sessionId = session.SessionId;
        _dcId = session.DcId;
        _testMode = session.TestMode;
        _serverTimeOffset = session.ServerTimeOffset;
        _lastSentMsgId = session.LastMsgId;
        _seqnoCounter = session.SeqNo;
        _updatePts = session.Pts;
        _updateQts = session.Qts;
        _updateSeq = session.Seq;
        _updateDate = session.Date == 0
            ? default
            : DateTimeOffset.FromUnixTimeSeconds(session.Date).UtcDateTime;
    }

    /// <summary>Живо ли транспортное соединение (иначе — переподключение).</summary>
    public bool IsConnected => _transport?.IsConnected ?? false;

    public void Dispose()
    {
        _transport?.Dispose();
        GC.SuppressFinalize(this);
    }
}
