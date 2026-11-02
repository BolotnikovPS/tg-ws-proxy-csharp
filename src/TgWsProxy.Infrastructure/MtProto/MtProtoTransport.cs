#nullable disable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// MTProto TCP transport (intermediate mode) with MTProxy transport-obfuscation support.
/// https://core.telegram.org/mtproto/mtproto-transports#transport-obfuscation
/// </summary>
public class MtProtoTransport(string host, int port, byte[] secret = null) : IDisposable
{
    private TcpClient _tcp;
    private NetworkStream _stream;
    private readonly string _host = host;
    private readonly int _port = port;

    // 16-byte MTProxy secret (dd/ee prefix already stripped); null = direct DC connection.
    private readonly byte[] _secret = secret;
    private IncrementalCipher _sendCtr;
    private IncrementalCipher _recvCtr;
    private bool _obfuscationInitialized;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host, _port, ct);
        _stream = _tcp.GetStream();

        if (_secret != null)
        {
            InitObfuscation();
        }
        else
        {
            // Intermediate transport protocol ID (0xEE = non-padded).
            await _stream.WriteAsync(new byte[] { 0xEE, 0xEE, 0xEE, 0xEE }, ct);
            await _stream.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Жив ли транспорт: соединение открыто и удалённая сторона не отправила EOF. Проверка
    /// <c>Poll(0, SelectRead)</c> + <c>Available == 0</c> обнаруживает закрытие сокета, которое
    /// <see cref="TcpClient.Connected"/> само по себе не отражает между операциями.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            var tcp = _tcp;
            if (tcp is null)
            {
                return false;
            }

            try
            {
                var socket = tcp.Client;
                return tcp.Connected
                    && socket is not null
                    && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// MTProxy transport obfuscation: generates the 64-byte preamble and derives the
    /// send/recv AES-CTR streams. Mirrors the server side (<c>MtProtoInspector.TryHandshake</c>)
    /// and the WTelegramClient reference <c>InitObfuscation</c>.
    /// </summary>
    private void InitObfuscation()
    {
        if (_secret.Length != 16)
        {
            throw new ArgumentException("MTProxy secret must be 16 bytes (dd/ee prefix stripped).", nameof(secret));
        }

        // Generate a 64-byte preamble, re-rolling while it collides with reserved signatures.
        var preamble = new byte[64];
        do
        {
            RandomNumberGenerator.Fill(preamble.AsSpan(0, 58));
        }
        while (preamble[0] == 0xef ||
               BinaryPrimitives.ReadUInt32LittleEndian(preamble) is 0x44414548 or 0x54534f50 or 0x20544547 or 0x4954504f or 0x02010316 or 0xdddddddd or 0xeeeeeeee ||
               BinaryPrimitives.ReadInt32LittleEndian(preamble.AsSpan(4)) == 0);

        // Tail (bytes 56..64): copy the tag prefix, then proto tag + dc index.
        preamble[62] = preamble[56];
        preamble[63] = preamble[57];
        preamble[56] = preamble[57] = preamble[58] = preamble[59] = 0xEE; // intermediate transport
        preamble[60] = 0; // dc id (little-endian; 0 = default/nearest DC)
        preamble[61] = 0;

        // Derive keys: recvKey = preamble[8..40], sendKey = reversed(preamble[8..40]).
        // The preamble is sent with bytes 8..56 reversed; the server reversing again
        // recovers the original for the recv direction.
        var recvKey = preamble[8..40];
        var recvIv = preamble[40..56];
        Array.Reverse(preamble, 8, 48);
        var sendKey = preamble[8..40];
        var sendIv = preamble[40..56];

        using (var sha256 = SHA256.Create())
        {
            sha256.TransformBlock(sendKey, 0, 32, null, 0);
            sha256.TransformFinalBlock(_secret, 0, 16);
            sendKey = sha256.Hash;
            sha256.Initialize();
            sha256.TransformBlock(recvKey, 0, 32, null, 0);
            sha256.TransformFinalBlock(_secret, 0, 16);
            recvKey = sha256.Hash;
        }

        _sendCtr = new IncrementalCipher(sendKey, sendIv);
        _recvCtr = new IncrementalCipher(recvKey, recvIv);

        // Encrypt only the last 8 bytes with the send cipher.
        var encrypted = (byte[])preamble.Clone();
        _sendCtr.UpdateInPlace(encrypted);
        for (var i = 56; i < 64; i++)
        {
            preamble[i] = encrypted[i];
        }

        _stream.Write(preamble, 0, 64);
        _stream.Flush();
        _obfuscationInitialized = true;
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_obfuscationInitialized && _sendCtr != null)
        {
            var packet = (byte[])data.Clone();
            _sendCtr.UpdateInPlace(packet);
            await _stream.WriteAsync(packet, ct);
        }
        else
        {
            await _stream.WriteAsync(data, ct);
        }

        await _stream.FlushAsync(ct);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        // Intermediate transport: first 4 bytes = payload length (little-endian).
        var lenBuf = new byte[4];
        var read = await _stream.ReadAsync(lenBuf, ct);
        if (read == 0)
        {
            throw new IOException("Connection closed");
        }

        if (read < 4)
        {
            var remaining = new byte[4 - read];
            await _stream.ReadExactlyAsync(remaining, ct);
            Array.Copy(remaining, 0, lenBuf, read, remaining.Length);
        }

        if (_obfuscationInitialized && _recvCtr != null)
        {
            _recvCtr.UpdateInPlace(lenBuf);
        }

        var payloadLen = BitConverter.ToInt32(lenBuf, 0);
        if (payloadLen is <= 0 or > (1024 * 1024))
        {
            throw new IOException($"Invalid payload length: {payloadLen}");
        }

        var data = new byte[payloadLen];
        await _stream.ReadExactlyAsync(data, ct);

        if (_obfuscationInitialized && _recvCtr != null)
        {
            _recvCtr.UpdateInPlace(data);
        }

        // Re-attach the length prefix to match the caller's expected format.
        var result = new byte[4 + payloadLen];
        Array.Copy(lenBuf, 0, result, 0, 4);
        Array.Copy(data, 0, result, 4, payloadLen);
        return result;
    }

    public void Dispose()
    {
        _sendCtr?.Dispose();
        _recvCtr?.Dispose();
        _stream?.Dispose();
        _tcp?.Dispose();
        GC.SuppressFinalize(this);
    }
}
