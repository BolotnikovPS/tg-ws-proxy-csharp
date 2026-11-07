#nullable disable

// Core methods invoked directly by the engine (vs the auto-generated schema methods in Methods.cs):
// PQ/DH handshake, session lifecycle, RPC wrappers (invokeWithLayer/initConnection), bot bootstrap.

using System;
using TgWsProxy.Infrastructure.MtProto.TL.Abstractions;
using TgWsProxy.Infrastructure.MtProto.TL.MtProtos;

namespace TgWsProxy.Infrastructure.MtProto.TL;

[TLDef(0x60469778)] //req_pq#60469778 nonce:int128 = ResPQ
public sealed partial class ReqPq : IMethod<ResPQ>
{
    public Int128 nonce;
}

[TLDef(0xBE7E8EF1)] //req_pq_multi#be7e8ef1 nonce:int128 = ResPQ
public sealed partial class ReqPqMulti : IMethod<ResPQ>
{
    public Int128 nonce;
}

[TLDef(0xD712E4BE)] //req_DH_params#d712e4be nonce:int128 server_nonce:int128 p:bytes q:bytes public_key_fingerprint:long encrypted_data:bytes = Server_DH_Params
public sealed partial class ReqDHParams : IMethod<ServerDHParams>
{
    public Int128 nonce;
    public Int128 server_nonce;
    public byte[] p;
    public byte[] q;
    public long public_key_fingerprint;
    public byte[] encrypted_data;
}

[TLDef(0xF5045F1F)] //set_client_DH_params#f5045f1f nonce:int128 server_nonce:int128 encrypted_data:bytes = Set_client_DH_params_answer
public sealed partial class SetClientDHParams : IMethod<SetClientDHParamsAnswer>
{
    public Int128 nonce;
    public Int128 server_nonce;
    public byte[] encrypted_data;
}

[TLDef(0xD1435160)] //destroy_auth_key#d1435160 = DestroyAuthKeyRes
public sealed partial class DestroyAuthKey : IMethod<DestroyAuthKeyRes> { }

[TLDef(0x58E4A740)] //rpc_drop_answer#58e4a740 req_msg_id:long = RpcDropAnswer
public partial class RpcDropAnswer : IMethod<RpcDropAnswer>
{
    public long req_msg_id;
}

[TLDef(0xB921BD04)] //get_future_salts#b921bd04 num:int = FutureSalts
public sealed partial class GetFutureSalts : IMethod<FutureSalts>
{
    public int num;
}

[TLDef(0x7ABE77EC)] //ping#7abe77ec ping_id:long = Pong
public sealed partial class Ping : IMethod<Pong>
{
    public long ping_id;
}

[TLDef(0xF3427B8C)] //ping_delay_disconnect#f3427b8c ping_id:long disconnect_delay:int = Pong
public sealed partial class PingDelayDisconnect : IMethod<Pong>
{
    public long ping_id;
    public int disconnect_delay;
}

[TLDef(0xE7512126)] //destroy_session#e7512126 session_id:long = DestroySessionRes
public sealed partial class DestroySession : IMethod<DestroySessionRes>
{
    public long session_id;
}

[TLDef(0x67A3FF2C)] //auth.importBotAuthorization#67a3ff2c flags:# api_id:int api_hash:string bot_auth_token:string = auth.Authorization
public sealed partial class ImportBotAuthorization : IMethod<Auth_AuthorizationBase>
{
    public int flags;
    public int api_id;
    public string api_hash;
    public string bot_auth_token;
}

[TLDef(0x0D91A548)] //users.getUsers#d91a548 id:Vector<InputUser> = Vector<User>
public sealed partial class Users_GetUsers : IMethod<UserBase[]>
{
    public InputUserBase[] id;
}

[TLDef(0xEDD4882A)] //updates.getState#edd4882a = updates.State
public sealed partial class Updates_GetState : IMethod<Updates_State> { }

[TLDef(0x19C2F763)] //updates.getDifference#19c2f763 flags:# pts:int pts_limit:flags.1?int pts_total_limit:flags.0?int date:int qts:int qts_limit:flags.2?int = updates.Difference
public sealed partial class Updates_GetDifference : IMethod<Updates_DifferenceBase>
{
    public Flags flags;
    public int pts;
    [IfFlag(1)] public int pts_limit;
    [IfFlag(0)] public int pts_total_limit;
    public DateTime date;
    public int qts;
    [IfFlag(2)] public int qts_limit;

    [Flags]
    public enum Flags : uint
    {
        has_pts_total_limit = 0x1,
        has_pts_limit = 0x2,
        has_qts_limit = 0x4,
    }
}

[TLDef(0xDA9B0D0D)] //invokeWithLayer#da9b0d0d {X:Type} layer:int query:!X = X
public sealed partial class InvokeWithLayer : IObject
{
    public int layer;
    public IObject query;
}

[TLDef(0xC1CD5EA9)] //initConnection#c1cd5ea9 {X:Type} flags:# api_id:int device_model:string system_version:string app_version:string system_lang_code:string lang_pack:string lang_code:string proxy:flags.0?InputClientProxy params:flags.1?JSONValue query:!X = X
public sealed partial class InitConnection : IObject
{
    public int flags;
    public int api_id;
    public string device_model;
    public string system_version;
    public string app_version;
    public string system_lang_code;
    public string lang_pack;
    public string lang_code;
    public IObject query;
}
