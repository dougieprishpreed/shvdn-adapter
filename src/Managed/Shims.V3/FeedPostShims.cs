using GTA.Graphics;
using GTA.Native;
using GTA.UI;

namespace ShvdnCompat.Shims.V3
{
    /// <summary>
    /// <c>GTA.FeedPost</c> as it existed before it moved to <c>GTA.UI.FeedPost</c>
    /// (v3.7.0 nightlies up to at least nightly.26).
    /// </summary>
    [InjectType("GTA.FeedPost")]
    public sealed class LegacyFeedPost
    {
        internal LegacyFeedPost(int handle)
        {
            Handle = handle;
        }

        public int Handle { get; }

        public void Delete()
        {
            Function.Call(Hash.THEFEED_REMOVE_ITEM, Handle);
        }

        public override bool Equals(object obj)
        {
            return obj is LegacyFeedPost other && other.Handle == Handle;
        }

        public static bool operator ==(LegacyFeedPost left, LegacyFeedPost right)
        {
            return left is null ? right is null : left.Equals(right);
        }

        public static bool operator !=(LegacyFeedPost left, LegacyFeedPost right)
        {
            return !(left == right);
        }

        public static implicit operator InputArgument(LegacyFeedPost value)
        {
            return new InputArgument((ulong)(value is null ? -1 : value.Handle));
        }

        public override int GetHashCode()
        {
            return Handle.GetHashCode();
        }

        internal static LegacyFeedPost FromCurrent(FeedPost post)
        {
            return post is null ? null : new LegacyFeedPost(post.Handle);
        }
    }

    /// <summary>The <c>Notification.Post*</c> overloads that returned <c>GTA.FeedPost</c>.</summary>
    [ShimType("GTA.UI.Notification")]
    internal static class NotificationShims
    {
        [Shim]
        public static LegacyFeedPost PostTicker(string message, bool isImportant, bool cacheMessage)
        {
            return LegacyFeedPost.FromCurrent(Notification.PostTicker(message, isImportant, cacheMessage));
        }

        [Shim]
        public static LegacyFeedPost PostTickerForced(string message, bool isImportant, bool cacheMessage)
        {
            return LegacyFeedPost.FromCurrent(Notification.PostTickerForced(message, isImportant, cacheMessage));
        }

        [Shim]
        public static LegacyFeedPost PostTickerWithTokens(string message, bool isImportant, bool cacheMessage)
        {
            return LegacyFeedPost.FromCurrent(Notification.PostTickerWithTokens(message, isImportant, cacheMessage));
        }

        [Shim]
        public static LegacyFeedPost PostMessageText(string message, TextureAsset texAsset, bool isImportant, FeedTextIcon icon, string characterName, string subtitle)
        {
            return LegacyFeedPost.FromCurrent(Notification.PostMessageText(message, texAsset, isImportant, icon, characterName, subtitle));
        }

        [Shim]
        public static LegacyFeedPost PostUnlock(string message, string title, FeedUnlockIcon iconType)
        {
            return LegacyFeedPost.FromCurrent(Notification.PostUnlock(message, title, iconType));
        }

        [Shim]
        public static LegacyFeedPost PostUnlockTitleUpdate(string message, string title, FeedUnlockIcon iconType, bool isImportant)
        {
            return LegacyFeedPost.FromCurrent(Notification.PostUnlockTitleUpdate(message, title, iconType, isImportant));
        }

        [Shim]
        public static LegacyFeedPost PostUnlockTitleUpdateWithColor(string message, string title, FeedUnlockIcon iconType, bool isImportant, HudColor titleColor, bool titleIsLiteral)
        {
            return LegacyFeedPost.FromCurrent(Notification.PostUnlockTitleUpdateWithColor(message, title, iconType, isImportant, titleColor, titleIsLiteral));
        }

        [Shim]
        public static LegacyFeedPost PostVersusTitleUpdate(TextureAsset char1TexAsset, int val1, TextureAsset char2TexAsset, int val2, HudColor customColor1, HudColor customColor2)
        {
            return LegacyFeedPost.FromCurrent(Notification.PostVersusTitleUpdate(char1TexAsset, val1, char2TexAsset, val2, customColor1, customColor2));
        }

        [Shim]
        public static LegacyFeedPost PostAward(string message, TextureAsset texAsset, int xp, HudColor awardColor, string title)
        {
            return LegacyFeedPost.FromCurrent(Notification.PostAward(message, texAsset, xp, awardColor, title));
        }
    }
}
