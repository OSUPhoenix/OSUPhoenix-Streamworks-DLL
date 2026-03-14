using Streamer.bot.Plugin.Interface.Model;

namespace OSWTools
{
    /// <summary>
    /// Twitch-specific helper methods.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///   string pic  = lib.GetTwitchProfilePicture("osuphoenix");
    ///   var    user = lib.GetTwitchUserInfo("osuphoenix");
    /// </summary>
    public partial class OSWLib
    {
        /// <summary>
        /// Returns the profile image URL for a Twitch user by login name.
        /// Returns an empty string if the user is not found.
        /// </summary>
        public string GetTwitchProfilePicture(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return string.Empty;
            try
            {
                TwitchUserInfoEx user = _CPH.TwitchGetExtendedUserInfoByLogin(userName);
                if (user == null) return string.Empty;
                return user.ProfileImageUrl ?? string.Empty;
            }
            catch
            {
                LogWarn("GetTwitchProfilePicture failed for: " + userName);
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns the profile image URL for a Twitch user by their user ID.
        /// Returns an empty string if the user is not found.
        /// </summary>
        public string GetTwitchProfilePictureById(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return string.Empty;
            try
            {
                TwitchUserInfoEx user = _CPH.TwitchGetExtendedUserInfoById(userId);
                if (user == null) return string.Empty;
                return user.ProfileImageUrl ?? string.Empty;
            }
            catch
            {
                LogWarn("GetTwitchProfilePictureById failed for ID: " + userId);
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns the full extended user info for a Twitch user by login name.
        /// Returns null if the user is not found.
        /// </summary>
        public TwitchUserInfoEx GetTwitchUserInfo(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return null;
            try
            {
                return _CPH.TwitchGetExtendedUserInfoByLogin(userName);
            }
            catch
            {
                LogWarn("GetTwitchUserInfo failed for: " + userName);
                return null;
            }
        }

        /// <summary>
        /// Returns the display name for a Twitch user by login name.
        /// Falls back to the login name itself if the API call fails.
        /// </summary>
        public string GetTwitchDisplayName(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return userName ?? string.Empty;
            try
            {
                TwitchUserInfoEx user = _CPH.TwitchGetExtendedUserInfoByLogin(userName);
                if (user != null && !string.IsNullOrWhiteSpace(user.UserName))
                    return user.UserName;
                return userName;
            }
            catch
            {
                return userName;
            }
        }
    }
}
