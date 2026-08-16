using System;

namespace ClanSystem.CoreData
{
    /// <summary>
    /// Conversions between the wire strings used by Cloud Code and the client enums.
    /// Keeping them here stops role and channel literals from leaking across the codebase.
    /// </summary>
    public static class SocialEnum
    {
        public const string RoleOwner = "Owner";
        public const string RoleOfficer = "Officer";
        public const string RoleMember = "Member";

        public const string ChannelGlobal = "global";
        public const string ChannelClan = "clan";

        public static ClanRole ParseRole(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return ClanRole.None;
            }

            if (string.Equals(value, RoleOwner, StringComparison.OrdinalIgnoreCase))
            {
                return ClanRole.Owner;
            }

            if (string.Equals(value, RoleOfficer, StringComparison.OrdinalIgnoreCase))
            {
                return ClanRole.Officer;
            }

            if (string.Equals(value, RoleMember, StringComparison.OrdinalIgnoreCase))
            {
                return ClanRole.Member;
            }

            return ClanRole.None;
        }

        public static string ToWire(ClanRole role)
        {
            switch (role)
            {
                case ClanRole.Owner: return RoleOwner;
                case ClanRole.Officer: return RoleOfficer;
                case ClanRole.Member: return RoleMember;
                default: return string.Empty;
            }
        }

        public static string ToWire(ChatChannel channel)
        {
            return channel == ChatChannel.Clan ? ChannelClan : ChannelGlobal;
        }
    }
}
