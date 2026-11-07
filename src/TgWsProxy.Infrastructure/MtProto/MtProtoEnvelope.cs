#nullable disable

using TgWsProxy.Infrastructure.MtProto.TL.Abstractions;

namespace TgWsProxy.Infrastructure.MtProto;

// Below TL types are commented "parsed manually" from https://github.com/telegramdesktop/tdesktop/blob/dev/Telegram/Resources/tl/mtproto.tl

[TLDef(0x7A19CB76)] //RSA_public_key#7a19cb76 n:bytes e:bytes = RSAPublicKey
public sealed partial class RSAPublicKey : IObject
{
    public byte[] n;
    public byte[] e;
}

[TLDef(0xF35C6D01)] //rpc_result#f35c6d01 req_msg_id:long result:Object = RpcResult
public sealed partial class RpcResult : IObject
{
    public long req_msg_id;
    public object result;
}

[TLDef(0x5BB8E511)] //message#5bb8e511 msg_id:long seqno:int bytes:int body:Object = Message
public sealed partial class Msg(long msgId, int seqno, IObject obj) : IObject
{
    // parameterless ctor: required by reflection-based ReadTLObject (Activator.CreateInstance)
    public Msg() : this(0, 0, null) { }

    public long msg_id = msgId;
    public int seqno = seqno;
    public int bytes;
    public IObject body = obj;
}

[TLDef(0x73F1F8DC)] //msg_container#73f1f8dc messages:vector<%Message> = MessageContainer
public sealed partial class MsgContainer : IObject { public List<Msg> messages; }
[TLDef(0xE06046B2)] //msg_copy#e06046b2 orig_message:Message = MessageCopy
public sealed partial class MsgCopy : IObject { public Msg orig_message; }

[TLDef(0x3072CFA1)] //gzip_packed#3072cfa1 packed_data:bytes = Object
public sealed partial class GzipPacked : IObject { public byte[] packed_data; }
