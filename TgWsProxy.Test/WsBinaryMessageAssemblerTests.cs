#nullable enable

using System.IO;
using TgWsProxy.Infrastructure;

namespace TgWsProxy.Test;

public class WsBinaryMessageAssemblerTests
{
    [Fact]
    public void StartFrame_WithFin_ReturnsPayload()
    {
        var asm = new WsBinaryMessageAssembler();
        var payload = new byte[] { 1, 2, 3 };

        var result = asm.OnFrame(fin: true, opcode: 0x2, payload: payload);

        Assert.NotNull(result);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void StartFrame_WithoutFin_AndContinuationFrames_ReturnsReassembledPayload()
    {
        var asm = new WsBinaryMessageAssembler();

        var p1 = new byte[] { 0x10, 0x11 };
        var p2 = new byte[] { 0x20 };
        var p3 = new byte[] { 0x30, 0x31, 0x32 };

        var r1 = asm.OnFrame(fin: false, opcode: 0x1, payload: p1);
        Assert.Null(r1);

        var r2 = asm.OnFrame(fin: false, opcode: 0x0, payload: p2);
        Assert.Null(r2);

        var r3 = asm.OnFrame(fin: true, opcode: 0x0, payload: p3);
        Assert.NotNull(r3);
        Assert.Equal(new byte[] { 0x10, 0x11, 0x20, 0x30, 0x31, 0x32 }, r3);
    }

    [Fact]
    public void ContinuationFrame_WithoutStart_Throws()
    {
        var asm = new WsBinaryMessageAssembler();
        Assert.Throws<IOException>(() => asm.OnFrame(fin: true, opcode: 0x0, payload: [1, 2]));
    }

    [Fact]
    public void StartFrame_WhileFragmentInProgress_Throws()
    {
        var asm = new WsBinaryMessageAssembler();

        var p1 = new byte[] { 0x01 };
        var p2 = new byte[] { 0x02 };

        asm.OnFrame(fin: false, opcode: 0x2, payload: p1);

        Assert.Throws<IOException>(() => asm.OnFrame(fin: true, opcode: 0x1, payload: p2));
    }
}

