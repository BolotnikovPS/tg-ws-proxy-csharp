namespace TgWsProxy.Application.Abstractions;

public interface IRawWebSocket
{
    Task Send(byte[] data);

    Task<byte[]?> Recv();

    Task Close();
}
