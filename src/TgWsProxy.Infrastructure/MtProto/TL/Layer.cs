#nullable disable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using TgWsProxy.Infrastructure.MtProto.TL.Abstractions;

namespace TgWsProxy.Infrastructure.MtProto.TL;

public static partial class Layer
{
    public const int Version = 229;                 // fetched 08/26/2026 00:30:42
    internal const uint VectorCtor = 0x1CB5C415;
    internal const uint NullCtor = 0x56730BCC;
    internal const uint RpcResultCtor = 0xF35C6D01;
    internal const uint RpcErrorCtor = 0x2144CA19;
    internal const uint MsgContainerCtor = 0x73F1F8DC;
    internal const uint BadMsgCtor = 0xA7EFF811;
    internal const uint GZipedCtor = 0x3072CFA1;

    // type -> ctor of its "empty"/"notModified"/"default" variant, used by Serialization.WriteTLNull
    // when a null value is written for a field of a polymorphic base type.
    internal static readonly Dictionary<Type, uint> Nullables = new()
    {
        // from TL.MTProto:
        // from TL.Schema:
        [typeof(Null)] = 0x56730BCC, //null
        [typeof(InputPeer)] = 0x7F3B18EA, //inputPeerEmpty
        [typeof(InputUserBase)] = 0xB98886CF, //inputUserEmpty
        [typeof(InputMedia)] = 0x9664F57F, //inputMediaEmpty
        [typeof(InputChatPhotoBase)] = 0x1CA48F57, //inputChatPhotoEmpty
        [typeof(InputGeoPoint)] = 0xE4C123D6, //inputGeoPointEmpty
        [typeof(InputPhoto)] = 0x1CD7BF0D, //inputPhotoEmpty
        [typeof(UserProfilePhoto)] = 0x4F11BAE1, //userProfilePhotoEmpty
        [typeof(UserStatus)] = 0x09D05049, //userStatusEmpty
        [typeof(ChatPhoto)] = 0x37C1011C, //chatPhotoEmpty
        [typeof(MessageMedia)] = 0x3DED6320, //messageMediaEmpty
        [typeof(MessageAction)] = 0xB6AEF7B0, //messageActionEmpty
        [typeof(GeoPoint)] = 0x1117DD5F, //geoPointEmpty
        [typeof(Contacts_Contacts)] = 0xB74BA9D2, //contacts.contactsNotModified
        [typeof(MessagesFilter)] = 0x57E2F66C, //inputMessagesFilterEmpty
        [typeof(Help_AppUpdate)] = 0xC45A6536, //help.noAppUpdate
        [typeof(EncryptedFile)] = 0xC21F497E, //encryptedFileEmpty
        [typeof(InputEncryptedFileBase)] = 0x1837C364, //inputEncryptedFileEmpty
        [typeof(InputDocument)] = 0x72F0EAAE, //inputDocumentEmpty
        [typeof(Messages_Stickers)] = 0xF1749A22, //messages.stickersNotModified
        [typeof(Messages_AllStickers)] = 0xE86602C3, //messages.allStickersNotModified
        [typeof(InputStickerSet)] = 0xFFB62B95, //inputStickerSetEmpty
        [typeof(Messages_StickerSet)] = 0xD3F924EB, //messages.stickerSetNotModified
        [typeof(InputChannelBase)] = 0xEE8C1E86, //inputChannelEmpty
        [typeof(ChannelMessagesFilter)] = 0x94D42EE7, //channelMessagesFilterEmpty
        [typeof(Channels_ChannelParticipants)] = 0xF0173FE9, //channels.channelParticipantsNotModified
        [typeof(Messages_SavedGifs)] = 0xE8025CA2, //messages.savedGifsNotModified
        [typeof(Contacts_TopPeersBase)] = 0xDE266EF5, //contacts.topPeersNotModified
        [typeof(Messages_RecentStickers)] = 0x0B17F890, //messages.recentStickersNotModified
        [typeof(RichText)] = 0xDC3D824F, //textEmpty
        [typeof(Messages_FavedStickers)] = 0x9E8FA6D3, //messages.favedStickersNotModified
        [typeof(Messages_FoundStickerSets)] = 0x0D54B65D, //messages.foundStickerSetsNotModified
        [typeof(SecureFile)] = 0x64199744, //secureFileEmpty
        [typeof(Help_DeepLinkInfo)] = 0x66AFA166, //help.deepLinkInfoEmpty
        [typeof(PasswordKdfAlgo)] = 0xD45AB096, //passwordKdfAlgoUnknown
        [typeof(SecurePasswordKdfAlgo)] = 0x004A8537, //securePasswordKdfAlgoUnknown
        [typeof(InputCheckPasswordSRP)] = 0x9880F658, //inputCheckPasswordEmpty
        [typeof(Help_PassportConfig)] = 0xBFB9F457, //help.passportConfigNotModified
        [typeof(Help_UserInfo)] = 0xF3AE2EED, //help.userInfoEmpty
        [typeof(Account_WallPapers)] = 0x1C199183, //account.wallPapersNotModified
        [typeof(UrlAuthResult)] = 0xA9D6DB1F, //urlAuthResultDefault
        [typeof(ChannelLocation)] = 0xBFB5AD8B, //channelLocationEmpty
        [typeof(Account_Themes)] = 0xF41EB622, //account.themesNotModified
        [typeof(DialogFilterBase)] = 0x363293AE, //dialogFilterDefault
        [typeof(Help_CountriesList)] = 0x93CC1F32, //help.countriesListNotModified
        [typeof(BotCommandScope)] = 0x2F6CB2AB, //botCommandScopeDefault
        [typeof(Messages_SponsoredMessages)] = 0x1839490F, //messages.sponsoredMessagesEmpty
        [typeof(Messages_AvailableReactions)] = 0x9F071957, //messages.availableReactionsNotModified
        [typeof(AttachMenuBots)] = 0xF1D88A5C, //attachMenuBotsNotModified
        [typeof(BotMenuButtonBase)] = 0x7533A588, //botMenuButtonDefault
        [typeof(Account_SavedRingtones)] = 0xFBF6E8B1, //account.savedRingtonesNotModified
        [typeof(NotificationSound)] = 0x97E8BEBE, //notificationSoundDefault
        [typeof(EmojiStatus)] = 0x2DE11AAE, //emojiStatusEmpty
        [typeof(Account_EmojiStatuses)] = 0xD08CE645, //account.emojiStatusesNotModified
        [typeof(Reaction)] = 0x79F5D419, //reactionEmpty
        [typeof(ChatReactions)] = 0xEAFC32BC, //chatReactionsNone
        [typeof(Messages_Reactions)] = 0xB06FDBDF, //messages.reactionsNotModified
                                                   // from TL.Secret:
        [typeof(Account_ChatThemes)] = 0xE011E1C4, //account.chatThemesNotModified
        [typeof(EmojiStatusBase)] = 0x2DE11AAE, //emojiStatusEmpty
        [typeof(EmojiList)] = 0x481EADFA, //emojiListNotModified
        [typeof(Messages_EmojiGroups)] = 0x6FB4AD87, //messages.emojiGroupsNotModified
        [typeof(Help_AppConfig)] = 0x7CDE641D, //help.appConfigNotModified
        [typeof(BotApp)] = 0x5DA674B7, //botAppNotModified
        [typeof(Help_PeerColors)] = 0x2BA1F5CE, //help.peerColorsNotModified
        [typeof(Messages_SavedReactionTags)] = 0x889B59EF, //messages.savedReactionTagsNotModified
        [typeof(Help_TimezonesList)] = 0x970708CC, //help.timezonesListNotModified
        [typeof(Messages_QuickReplies)] = 0x5F91EB5B, //messages.quickRepliesNotModified
        [typeof(Messages_AvailableEffects)] = 0xD1ED9A5B, //messages.availableEffectsNotModified
        [typeof(Payments_StarGifts)] = 0xA388A368, //payments.starGiftsNotModified
        [typeof(PaidReactionPrivacy)] = 0x206AD49E, //paidReactionPrivacyDefault
        [typeof(RequirementToContact)] = 0x050A9839, //requirementToContactEmpty
        [typeof(Contacts_SponsoredPeers)] = 0xEA32B4B1, //contacts.sponsoredPeersEmpty
        [typeof(Payments_StarGiftCollections)] = 0xA0BA4F17, //payments.starGiftCollectionsNotModified
        [typeof(Stories_Albums)] = 0x564EDAEB, //stories.albumsNotModified
        [typeof(Account_SavedMusicIds)] = 0x4FC81D6E, //account.savedMusicIdsNotModified
        [typeof(InputChatThemeBase)] = 0x83268483, //inputChatThemeEmpty
        [typeof(StarGiftAuctionStateBase)] = 0xFE333952, //starGiftAuctionStateNotModified
        [typeof(Payments_StarGiftActiveAuctions)] = 0xDB33DAD0, //payments.starGiftActiveAuctionsNotModified
        [typeof(Aicompose_Tones)] = 0xC1F46103, //aicompose.tonesNotModified
        [typeof(Account_WebBrowserSettings)] = 0xC31C8F4E, //account.webBrowserSettingsNotModified
        [typeof(ButtonType)] = 0xC9DD90E9, //buttonTypeDefault
        [typeof(Ephemeral_WelcomeMessages)] = 0x59FFDB31, //ephemeral.welcomeMessagesNotModified
        [typeof(DecryptedMessageMedia)] = 0x089F5C4A, //decryptedMessageMediaEmpty
    };

    // Ctors that deserialize to null (the "empty"/"notModified"/"default" variants). These were the
    // `[ctor] = null` entries of the former hand-written table.
    internal static readonly HashSet<uint> EmptyCtors =
    [
        0x004A8537, // SecurePasswordKdfAlgoUnknown
        0x050A9839, // RequirementToContactEmpty
        0x089F5C4A, // decryptedMessageMediaEmpty
        0x09D05049, // UserStatusEmpty
        0x0B17F890, // Messages_RecentStickersNotModified
        0x0D54B65D, // Messages_FoundStickerSetsNotModified
        0x1117DD5F, // GeoPointEmpty
        0x1837C364, // InputEncryptedFileEmpty
        0x1839490F, // Messages_SponsoredMessagesEmpty
        0x1C199183, // Account_WallPapersNotModified
        0x1CA48F57, // InputChatPhotoEmpty
        0x1CD7BF0D, // InputPhotoEmpty
        0x206AD49E, // PaidReactionPrivacyDefault
        0x2BA1F5CE, // Help_PeerColorsNotModified
        0x2DE11AAE, // EmojiStatusEmpty
        0x2F6CB2AB, // BotCommandScopeDefault
        0x363293AE, // DialogFilterDefault
        0x37C1011C, // ChatPhotoEmpty
        0x3DED6320, // MessageMediaEmpty
        0x481EADFA, // EmojiListNotModified
        0x4F11BAE1, // UserProfilePhotoEmpty
        0x4FC81D6E, // Account_SavedMusicIdsNotModified
        0x564EDAEB, // Stories_AlbumsNotModified
        0x56730BCC, // Null
        0x57E2F66C, // InputMessagesFilterEmpty
        0x59FFDB31, // Ephemeral_WelcomeMessagesNotModified
        0x5DA674B7, // BotAppNotModified
        0x5F91EB5B, // Messages_QuickRepliesNotModified
        0x64199744, // SecureFileEmpty
        0x66AFA166, // Help_DeepLinkInfoEmpty
        0x6FB4AD87, // Messages_EmojiGroupsNotModified
        0x72F0EAAE, // InputDocumentEmpty
        0x7533A588, // BotMenuButtonDefault
        0x79F5D419, // ReactionEmpty
        0x7CDE641D, // Help_AppConfigNotModified
        0x7F3B18EA, // InputPeerEmpty
        0x83268483, // InputChatThemeEmpty
        0x889B59EF, // Messages_SavedReactionTagsNotModified
        0x93CC1F32, // Help_CountriesListNotModified
        0x94D42EE7, // ChannelMessagesFilterEmpty
        0x9664F57F, // InputMediaEmpty
        0x970708CC, // Help_TimezonesListNotModified
        0x97E8BEBE, // NotificationSoundDefault
        0x9880F658, // InputCheckPasswordEmpty
        0x9E8FA6D3, // Messages_FavedStickersNotModified
        0x9F071957, // Messages_AvailableReactionsNotModified
        0xA0BA4F17, // Payments_StarGiftCollectionsNotModified
        0xA388A368, // Payments_StarGiftsNotModified
        0xA9D6DB1F, // UrlAuthResultDefault
        0xB06FDBDF, // Messages_ReactionsNotModified
        0xB6AEF7B0, // MessageActionEmpty
        0xB74BA9D2, // Contacts_ContactsNotModified
        0xB98886CF, // InputUserEmpty
        0xBFB5AD8B, // ChannelLocationEmpty
        0xBFB9F457, // Help_PassportConfigNotModified
        0xC1F46103, // Aicompose_TonesNotModified
        0xC21F497E, // EncryptedFileEmpty
        0xC31C8F4E, // Account_WebBrowserSettingsNotModified
        0xC45A6536, // Help_NoAppUpdate
        0xC9DD90E9, // ButtonTypeDefault
        0xD08CE645, // Account_EmojiStatusesNotModified
        0xD1ED9A5B, // Messages_AvailableEffectsNotModified
        0xD3F924EB, // Messages_StickerSetNotModified
        0xD45AB096, // PasswordKdfAlgoUnknown
        0xDB33DAD0, // Payments_StarGiftActiveAuctionsNotModified
        0xDC3D824F, // TextEmpty
        0xDE266EF5, // Contacts_TopPeersNotModified
        0xE011E1C4, // Account_ChatThemesNotModified
        0xE4C123D6, // InputGeoPointEmpty
        0xE8025CA2, // Messages_SavedGifsNotModified
        0xE86602C3, // Messages_AllStickersNotModified
        0xEA32B4B1, // Contacts_SponsoredPeersEmpty
        0xEAFC32BC, // ChatReactionsNone
        0xEE8C1E86, // InputChannelEmpty
        0xF0173FE9, // Channels_ChannelParticipantsNotModified
        0xF1749A22, // Messages_StickersNotModified
        0xF1D88A5C, // AttachMenuBotsNotModified
        0xF3AE2EED, // Help_UserInfoEmpty
        0xF41EB622, // Account_ThemesNotModified
        0xFBF6E8B1, // Account_SavedRingtonesNotModified
        0xFE333952, // StarGiftAuctionStateNotModified
        0xFFB62B95, // InputStickerSetEmpty
    ];

    // ctorNb -> concrete [TLDef] type, auto-discovered from the assembly. It can no longer drift
    // from the [TLDef] attributes (unlike the former hand-maintained 1650-line literal). RPC method
    // calls (IMethod<>) and the null markers (EmptyCtors) are excluded.
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static readonly IReadOnlyDictionary<uint, Type> Table = BuildTable();

    private static Dictionary<uint, Type> BuildTable()
    {
        var table = new Dictionary<uint, Type>();
        foreach (var type in typeof(Layer).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<TLDefAttribute>() is not { } def)
            {
                continue;
            }

            if (IsMethod(type))
            {
                continue;
            }

            if (EmptyCtors.Contains(def.CtorNb))
            {
                continue; // e.g. Null#56730bcc is read back as null
            }

            if (!table.TryAdd(def.CtorNb, type))
            {
                throw new InvalidOperationException($"Duplicate TL constructor #{def.CtorNb:x8}: {type} vs {table[def.CtorNb]}");
            }
        }
        return table;
    }

    private static bool IsMethod(Type type)
    {
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IMethod<>))
            {
                return true;
            }
        }

        return false;
    }
}
