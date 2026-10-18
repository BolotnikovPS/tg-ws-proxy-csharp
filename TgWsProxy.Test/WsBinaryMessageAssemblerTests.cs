using TgWsProxy.Infrastructure.Instances;

namespace TgWsProxy.Test;

public class WsBinaryMessageAssemblerTests
{
    private readonly CancellationToken cts = new CancellationTokenSource().Token;

    [Fact]
    public async Task StartFrame_WithFin_ReturnsPayload()
    {
        var asm = new WsBinaryMessageAssembler();
        var payload = new byte[] { 1, 2, 3 };

        var result = await asm.OnFrame(fin: true, opcode: 0x2, payload: payload, cts);

        Assert.NotNull(result);
        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task StartFrame_WithoutFin_AndContinuationFrames_ReturnsReassembledPayload()
    {
        var asm = new WsBinaryMessageAssembler();

        var p1 = new byte[] { 0x10, 0x11 };
        var p2 = " "u8.ToArray();
        var p3 = "012"u8.ToArray();

        var r1 = await asm.OnFrame(fin: false, opcode: 0x1, payload: p1, cts);
        Assert.Null(r1);

        var r2 = await asm.OnFrame(fin: false, opcode: 0x0, payload: p2, cts);
        Assert.Null(r2);

        var r3 = await asm.OnFrame(fin: true, opcode: 0x0, payload: p3, cts);
        Assert.NotNull(r3);
        Assert.Equal(new byte[] { 0x10, 0x11, 0x20, 0x30, 0x31, 0x32 }, r3);
    }

    [Fact]
    public async Task ContinuationFrame_WithoutStart_Throws()
    {
        var asm = new WsBinaryMessageAssembler();
        await Assert.ThrowsAsync<IOException>(async () => await asm.OnFrame(fin: true, opcode: 0x0, payload: [1, 2], cts));
    }

    [Fact]
    public async Task StartFrame_WhileFragmentInProgress_Throws()
    {
        var asm = new WsBinaryMessageAssembler();

        var p1 = new byte[] { 0x01 };
        var p2 = new byte[] { 0x02 };

        await asm.OnFrame(fin: false, opcode: 0x2, payload: p1, cts);

        await Assert.ThrowsAsync<IOException>(async () => await asm.OnFrame(fin: true, opcode: 0x1, payload: p2, cts));
    }

    [Fact]
    public async Task StartFrame_WithFin_ReturnsSameInstance_WithoutCopying()
    {
        var asm = new WsBinaryMessageAssembler();
        var payload = new byte[] { 0x42, 0x43 };

        var result = await asm.OnFrame(fin: true, opcode: 0x2, payload: payload, cts);

        // Нефрагментированный кадр — горячий путь: копии быть не должно.
        Assert.Same(payload, result);
    }

    [Fact]
    public async Task ContinuationFrames_GrowBuffer_BeyondInitialRent()
    {
        var asm = new WsBinaryMessageAssembler();

        var first = new byte[40_000];
        var second = new byte[40_000];
        for (var i = 0; i < first.Length; i++)
        {
            first[i] = 0x11;
            second[i] = 0x22;
        }

        Assert.Null(await asm.OnFrame(fin: false, opcode: 0x2, payload: first, cts));
        var result = await asm.OnFrame(fin: true, opcode: 0x0, payload: second, cts);

        Assert.NotNull(result);
        Assert.Equal(80_000, result!.Length);
        Assert.Equal(0x11, result[0]);
        Assert.Equal(0x11, result[39_999]);
        Assert.Equal(0x22, result[40_000]);
        Assert.Equal(0x22, result[79_999]);
    }

    [Fact]
    public async Task EmptyStartFrame_WithoutFin_ThenContinuation_ReturnsContinuationPayload()
    {
        var asm = new WsBinaryMessageAssembler();

        Assert.Null(await asm.OnFrame(fin: false, opcode: 0x2, payload: [], cts));

        var result = await asm.OnFrame(fin: true, opcode: 0x0, payload: [7, 8, 9], cts);

        Assert.Equal(new byte[] { 7, 8, 9 }, result);
    }

    [Fact]
    public async Task Reset_DropsPendingFragment_SoNextContinuationThrows()
    {
        var asm = new WsBinaryMessageAssembler();

        await asm.OnFrame(fin: false, opcode: 0x2, payload: [1, 2, 3], cts);
        asm.Reset();

        await Assert.ThrowsAsync<IOException>(async () => await asm.OnFrame(fin: true, opcode: 0x0, payload: [4], cts));
    }

    [Fact]
    public async Task Assembler_ReusableAfterCompletedMessage()
    {
        var asm = new WsBinaryMessageAssembler();

        await asm.OnFrame(fin: false, opcode: 0x2, payload: [1], cts);
        Assert.Equal(new byte[] { 1, 2 }, await asm.OnFrame(fin: true, opcode: 0x0, payload: [2], cts));

        // После завершения сообщения сборщик должен быть снова готов к работе.
        await asm.OnFrame(fin: false, opcode: 0x2, payload: [3], cts);
        Assert.Equal(new byte[] { 3, 4 }, await asm.OnFrame(fin: true, opcode: 0x0, payload: [4], cts));
    }
}
