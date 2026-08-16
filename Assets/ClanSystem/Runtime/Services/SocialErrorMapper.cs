using System;
using System.Collections.Generic;
using ClanSystem.CoreData;
using Unity.Services.Core;

namespace ClanSystem.Services
{
    /// <summary>
    /// Turns server error codes and SDK exceptions into a <see cref="SocialErrorCode"/> plus a
    /// message safe to show a player. Unmapped failures degrade to <see cref="SocialErrorCode.Unknown"/>
    /// rather than surfacing raw exception text.
    /// </summary>
    public static class SocialErrorMapper
    {
        private static readonly Dictionary<string, SocialErrorCode> _codes = new Dictionary<string, SocialErrorCode>(StringComparer.OrdinalIgnoreCase)
        {
            { "OK", SocialErrorCode.None },
            { "UNAUTHENTICATED", SocialErrorCode.NotSignedIn },
            { "PERMISSION_DENIED", SocialErrorCode.PermissionDenied },
            { "NOT_IN_CLAN", SocialErrorCode.NotInClan },
            { "ALREADY_IN_CLAN", SocialErrorCode.AlreadyInClan },
            { "ALREADY_MEMBER", SocialErrorCode.AlreadyMember },
            { "CLAN_NOT_FOUND", SocialErrorCode.ClanNotFound },
            { "CLAN_FULL", SocialErrorCode.ClanFull },
            { "CLAN_IS_PRIVATE", SocialErrorCode.ClanIsPrivate },
            { "INVALID_NAME", SocialErrorCode.InvalidName },
            { "INVALID_TAG", SocialErrorCode.InvalidTag },
            { "TAG_TAKEN", SocialErrorCode.TagTaken },
            { "INVITE_NOT_FOUND", SocialErrorCode.InviteNotFound },
            { "INVITE_EXPIRED", SocialErrorCode.InviteExpired },
            { "INVITE_EXISTS", SocialErrorCode.InviteExists },
            { "REQUEST_NOT_FOUND", SocialErrorCode.RequestNotFound },
            { "NOT_A_MEMBER", SocialErrorCode.NotAMember },
            { "OWNER_MUST_TRANSFER", SocialErrorCode.OwnerMustTransfer },
            { "RATE_LIMITED", SocialErrorCode.RateLimited },
            { "EMPTY_MESSAGE", SocialErrorCode.EmptyMessage },
            { "DUPLICATE_MESSAGE", SocialErrorCode.DuplicateMessage },
            { "INVALID_SCORE", SocialErrorCode.InvalidScore },
            { "LEADERBOARD_UNAVAILABLE", SocialErrorCode.LeaderboardUnavailable },
            { "INVALID_REQUEST", SocialErrorCode.InvalidRequest },
            { "UNKNOWN_ACTION", SocialErrorCode.InvalidRequest },
        };

        public static SocialErrorCode FromServerCode(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return SocialErrorCode.Unknown;
            }

            SocialErrorCode mapped;
            if (_codes.TryGetValue(code, out mapped))
            {
                return mapped;
            }

            return SocialErrorCode.Unknown;
        }

        public static SocialErrorCode FromException(Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return SocialErrorCode.Cancelled;
            }

            if (exception is TimeoutException)
            {
                return SocialErrorCode.Timeout;
            }

            RequestFailedException requestFailed = exception as RequestFailedException;
            if (requestFailed != null)
            {
                if (requestFailed.ErrorCode == CommonErrorCodes.TransportError
                    || requestFailed.ErrorCode == CommonErrorCodes.Timeout)
                {
                    return SocialErrorCode.NetworkUnavailable;
                }

                if (requestFailed.ErrorCode == CommonErrorCodes.TooManyRequests)
                {
                    return SocialErrorCode.RateLimited;
                }

                if (requestFailed.ErrorCode == CommonErrorCodes.Forbidden)
                {
                    return SocialErrorCode.PermissionDenied;
                }

                if (requestFailed.ErrorCode == CommonErrorCodes.InvalidToken
                    || requestFailed.ErrorCode == CommonErrorCodes.TokenExpired)
                {
                    return SocialErrorCode.NotSignedIn;
                }

                return SocialErrorCode.ServiceUnavailable;
            }

            return SocialErrorCode.Unknown;
        }

        /// <summary>
        /// Player-facing fallback text for a failure that arrived without a server message.
        /// </summary>
        public static string Describe(SocialErrorCode code)
        {
            switch (code)
            {
                case SocialErrorCode.NotSignedIn: return "You are signed out. Sign in and try again.";
                case SocialErrorCode.NetworkUnavailable: return "No connection. Check your network and retry.";
                case SocialErrorCode.Timeout: return "The request timed out. Try again.";
                case SocialErrorCode.ServiceUnavailable: return "The service is unavailable right now.";
                case SocialErrorCode.RateLimited: return "You are doing that too often. Slow down.";
                case SocialErrorCode.PermissionDenied: return "You do not have permission to do that.";
                case SocialErrorCode.NotInClan: return "You are not in a clan.";
                case SocialErrorCode.AlreadyInClan: return "You are already in a clan.";
                case SocialErrorCode.ClanNotFound: return "That clan no longer exists.";
                case SocialErrorCode.ClanFull: return "That clan is full.";
                case SocialErrorCode.ClanIsPrivate: return "That clan is invite-only.";
                case SocialErrorCode.InviteExpired: return "That invitation has expired.";
                case SocialErrorCode.InviteNotFound: return "That invitation is no longer available.";
                case SocialErrorCode.LeaderboardUnavailable: return "Leaderboards are not configured yet.";
                case SocialErrorCode.Cancelled: return "Cancelled.";
                default: return "Something went wrong. Please try again.";
            }
        }
    }
}
