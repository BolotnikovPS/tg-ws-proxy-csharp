#nullable disable
using System;

namespace TgWsProxy.Infrastructure.MtProto.TL.Methods
{
    [TLDef(0xFEF48F62)]
    public sealed partial class Messages_SendMessage : IMethod<UpdatesBase>
    {
        public Flags flags;
        public InputPeer peer;
        [IfFlag(0)] public InputReplyTo reply_to;
        public string message;
        public long random_id;
        [IfFlag(2)] public ReplyMarkup reply_markup;
        [IfFlag(3)] public MessageEntity[] entities;
        [IfFlag(10)] public DateTime schedule_date;
        [IfFlag(24)] public int schedule_repeat_period;
        [IfFlag(13)] public InputPeer send_as;
        [IfFlag(17)] public InputQuickReplyShortcutBase quick_reply_shortcut;
        [IfFlag(18)] public long effect;
        [IfFlag(21)] public long allow_paid_stars;
        [IfFlag(22)] public SuggestedPost suggested_post;
        [IfFlag(23)] public InputRichMessageBase rich_message;

        [Flags] public enum Flags : uint
        {
            has_reply_to = 0x1,
            no_webpage = 0x2,
            has_reply_markup = 0x4,
            has_entities = 0x8,
            silent = 0x20,
            background = 0x40,
            clear_draft = 0x80,
            has_schedule_date = 0x400,
            has_send_as = 0x2000,
            noforwards = 0x4000,
            update_stickersets_order = 0x8000,
            invert_media = 0x10000,
            has_quick_reply_shortcut = 0x20000,
            has_effect = 0x40000,
            allow_paid_floodskip = 0x80000,
            has_allow_paid_stars = 0x200000,
            has_suggested_post = 0x400000,
            has_rich_message = 0x800000,
            has_schedule_repeat_period = 0x1000000,
        }
    }

    [TLDef(0x0330E77F)]
    public sealed partial class Messages_SendMedia : IMethod<UpdatesBase>
    {
        public Flags flags;
        public InputPeer peer;
        [IfFlag(0)] public InputReplyTo reply_to;
        public InputMedia media;
        public string message;
        public long random_id;
        [IfFlag(2)] public ReplyMarkup reply_markup;
        [IfFlag(3)] public MessageEntity[] entities;
        [IfFlag(10)] public DateTime schedule_date;
        [IfFlag(24)] public int schedule_repeat_period;
        [IfFlag(13)] public InputPeer send_as;
        [IfFlag(17)] public InputQuickReplyShortcutBase quick_reply_shortcut;
        [IfFlag(18)] public long effect;
        [IfFlag(21)] public long allow_paid_stars;
        [IfFlag(22)] public SuggestedPost suggested_post;

        [Flags] public enum Flags : uint
        {
            has_reply_to = 0x1,
            has_reply_markup = 0x4,
            has_entities = 0x8,
            silent = 0x20,
            background = 0x40,
            clear_draft = 0x80,
            has_schedule_date = 0x400,
            has_send_as = 0x2000,
            noforwards = 0x4000,
            update_stickersets_order = 0x8000,
            invert_media = 0x10000,
            has_quick_reply_shortcut = 0x20000,
            has_effect = 0x40000,
            allow_paid_floodskip = 0x80000,
            has_allow_paid_stars = 0x200000,
            has_suggested_post = 0x400000,
            has_schedule_repeat_period = 0x1000000,
        }
    }

    [TLDef(0x1BF89D74)]
    public sealed partial class Messages_SendMultiMedia : IMethod<UpdatesBase>
    {
        public Flags flags;
        public InputPeer peer;
        [IfFlag(0)] public InputReplyTo reply_to;
        public InputSingleMedia[] multi_media;
        [IfFlag(10)] public DateTime schedule_date;
        [IfFlag(13)] public InputPeer send_as;
        [IfFlag(17)] public InputQuickReplyShortcutBase quick_reply_shortcut;
        [IfFlag(18)] public long effect;
        [IfFlag(21)] public long allow_paid_stars;

        [Flags] public enum Flags : uint
        {
            has_reply_to = 0x1,
            silent = 0x20,
            background = 0x40,
            clear_draft = 0x80,
            has_schedule_date = 0x400,
            has_send_as = 0x2000,
            noforwards = 0x4000,
            update_stickersets_order = 0x8000,
            invert_media = 0x10000,
            has_quick_reply_shortcut = 0x20000,
            has_effect = 0x40000,
            allow_paid_floodskip = 0x80000,
            has_allow_paid_stars = 0x200000,
        }
    }

    [TLDef(0xB304A621)]
    public sealed partial class Upload_SaveFilePart : IMethod<bool>
    {
        public long file_id;
        public int file_part;
        public byte[] bytes;
    }

    [TLDef(0xDE7B673D)]
    public sealed partial class Upload_SaveBigFilePart : IMethod<bool>
    {
        public long file_id;
        public int file_part;
        public int file_total_parts;
        public byte[] bytes;
    }

    [TLDef(0xBE5335BE)]
    public sealed partial class Upload_GetFile : IMethod<Upload_FileBase>
    {
        public Flags flags;
        public InputFileLocationBase location;
        public long offset;
        public int limit;

        [Flags] public enum Flags : uint
        {
            precise = 0x1,
            cdn_supported = 0x2,
        }
    }

    [TLDef(0xB106E66C)]
    public sealed partial class Messages_EditMessage : IMethod<UpdatesBase>
    {
        public Flags flags;
        public InputPeer peer;
        public int id;
        [IfFlag(11)] public string message;
        [IfFlag(14)] public InputMedia media;
        [IfFlag(2)] public ReplyMarkup reply_markup;
        [IfFlag(3)] public MessageEntity[] entities;
        [IfFlag(15)] public DateTime schedule_date;
        [IfFlag(18)] public int schedule_repeat_period;
        [IfFlag(17)] public int quick_reply_shortcut_id;
        [IfFlag(23)] public InputRichMessageBase rich_message;

        [Flags] public enum Flags : uint
        {
            no_webpage = 0x2,
            has_reply_markup = 0x4,
            has_entities = 0x8,
            has_message = 0x800,
            has_media = 0x4000,
            has_schedule_date = 0x8000,
            invert_media = 0x10000,
            has_quick_reply_shortcut_id = 0x20000,
            has_schedule_repeat_period = 0x40000,
            has_rich_message = 0x800000,
        }
    }

    [TLDef(0xE58E95D2)]
    public sealed partial class Messages_DeleteMessages : IMethod<Messages_AffectedMessages>
    {
        public Flags flags;
        public int[] id;

        [Flags] public enum Flags : uint
        {
            revoke = 0x1,
        }
    }

    [TLDef(0x13704A7C)]
    public sealed partial class Messages_ForwardMessages : IMethod<UpdatesBase>
    {
        public Flags flags;
        public InputPeer from_peer;
        public int[] id;
        public long[] random_id;
        public InputPeer to_peer;
        [IfFlag(9)] public int top_msg_id;
        [IfFlag(22)] public InputReplyTo reply_to;
        [IfFlag(10)] public DateTime schedule_date;
        [IfFlag(24)] public int schedule_repeat_period;
        [IfFlag(13)] public InputPeer send_as;
        [IfFlag(17)] public InputQuickReplyShortcutBase quick_reply_shortcut;
        [IfFlag(18)] public long effect;
        [IfFlag(20)] public int video_timestamp;
        [IfFlag(21)] public long allow_paid_stars;
        [IfFlag(23)] public SuggestedPost suggested_post;

        [Flags] public enum Flags : uint
        {
            silent = 0x20,
            background = 0x40,
            with_my_score = 0x100,
            has_top_msg_id = 0x200,
            has_schedule_date = 0x400,
            drop_author = 0x800,
            drop_media_captions = 0x1000,
            has_send_as = 0x2000,
            noforwards = 0x4000,
            has_quick_reply_shortcut = 0x20000,
            has_effect = 0x40000,
            allow_paid_floodskip = 0x80000,
            has_video_timestamp = 0x100000,
            has_allow_paid_stars = 0x200000,
            has_reply_to = 0x400000,
            has_suggested_post = 0x800000,
            has_schedule_repeat_period = 0x1000000,
            from_ephemeral = 0x2000000,
        }
    }

    [TLDef(0xD58F130A)]
    public sealed partial class Messages_SetBotCallbackAnswer : IMethod<bool>
    {
        public Flags flags;
        public long query_id;
        [IfFlag(0)] public string message;
        [IfFlag(2)] public string url;
        public int cache_time;

        [Flags] public enum Flags : uint
        {
            has_message = 0x1,
            alert = 0x2,
            has_url = 0x4,
        }
    }

    [TLDef(0x63C66506)]
    public sealed partial class Messages_GetMessages : IMethod<Messages_MessagesBase>
    {
        public InputMessage[] id;
    }

    [TLDef(0x49E9528F)]
    public sealed partial class Messages_GetChats : IMethod<Messages_Chats>
    {
        public long[] id;
    }

    [TLDef(0xAEB00B34)]
    public sealed partial class Messages_GetFullChat : IMethod<Messages_ChatFull>
    {
        public long chat_id;
    }

    [TLDef(0x73783FFD)]
    public sealed partial class Messages_EditChatTitle : IMethod<UpdatesBase>
    {
        public long chat_id;
        public string title;
    }

    [TLDef(0xDEF60797)]
    public sealed partial class Messages_EditChatAbout : IMethod<bool>
    {
        public InputPeer peer;
        public string about;
    }

    [TLDef(0x35DDD674)]
    public sealed partial class Messages_EditChatPhoto : IMethod<UpdatesBase>
    {
        public long chat_id;
        public InputChatPhotoBase photo;
    }

    [TLDef(0xA2185CAB)]
    public sealed partial class Messages_DeleteChatUser : IMethod<UpdatesBase>
    {
        public Flags flags;
        public long chat_id;
        public InputUserBase user_id;

        [Flags] public enum Flags : uint
        {
            revoke_history = 0x1,
        }
    }

    [TLDef(0xA0AB6CC6)]
    public sealed partial class Channels_GetParticipant : IMethod<Channels_ChannelParticipant>
    {
        public InputChannelBase channel;
        public InputPeer participant;
    }

    [TLDef(0x77CED9D0)]
    public sealed partial class Channels_GetParticipants : IMethod<Channels_ChannelParticipants>
    {
        public InputChannelBase channel;
        public ChannelParticipantsFilter filter;
        public int offset;
        public int limit;
        public long hash;
    }

    [TLDef(0x96E6CD81)]
    public sealed partial class Channels_EditBanned : IMethod<UpdatesBase>
    {
        public InputChannelBase channel;
        public InputPeer participant;
        public ChatBannedRights banned_rights;
    }

    [TLDef(0x0A7F6BBB)]
    public sealed partial class Channels_GetChannels : IMethod<Messages_Chats>
    {
        public InputChannelBase[] id;
    }

    [TLDef(0x08736A09)]
    public sealed partial class Channels_GetFullChannel : IMethod<Messages_ChatFull>
    {
        public InputChannelBase channel;
    }

    [TLDef(0x58943EE2)]
    public sealed partial class Messages_SetTyping : IMethod<bool>
    {
        public Flags flags;
        public InputPeer peer;
        [IfFlag(0)] public int top_msg_id;
        public SendMessageAction action;

        [Flags] public enum Flags : uint
        {
            has_top_msg_id = 0x1,
        }
    }

    [TLDef(0x0517165A)]
    public sealed partial class Bots_SetBotCommands : IMethod<bool>
    {
        public BotCommandScope scope;
        public string lang_code;
        public BotCommand[] commands;
    }

    [TLDef(0xE34C0DD6)]
    public sealed partial class Bots_GetBotCommands : IMethod<BotCommand[]>
    {
        public BotCommandScope scope;
        public string lang_code;
    }

    [TLDef(0xBB12A419)]
    public sealed partial class Messages_SetInlineBotResults : IMethod<bool>
    {
        public Flags flags;
        public long query_id;
        public InputBotInlineResultBase[] results;
        public int cache_time;
        [IfFlag(2)] public string next_offset;
        [IfFlag(3)] public InlineBotSwitchPM switch_pm;
        [IfFlag(4)] public InlineBotWebView switch_webview;

        [Flags] public enum Flags : uint
        {
            gallery = 0x1,
            private_ = 0x2,
            has_next_offset = 0x4,
            has_switch_pm = 0x8,
            has_switch_webview = 0x10,
        }
    }

    [TLDef(0x566DECD0)]
    public sealed partial class Channels_EditTitle : IMethod<UpdatesBase>
    {
        public InputChannelBase channel;
        public string title;
    }

    [TLDef(0xF12E57C9)]
    public sealed partial class Channels_EditPhoto : IMethod<UpdatesBase>
    {
        public InputChannelBase channel;
        public InputChatPhotoBase photo;
    }

    [TLDef(0xF836AA95)]
    public sealed partial class Channels_LeaveChannel : IMethod<UpdatesBase>
    {
        public InputChannelBase channel;
    }
}