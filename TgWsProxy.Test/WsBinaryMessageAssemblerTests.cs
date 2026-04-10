#nullable enable

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
}
