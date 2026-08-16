namespace ClanSystem.CoreData
{
    /// <summary>
    /// Every failure the social backend can report. String codes returned by Cloud Code are
    /// mapped onto this enum so the UI never has to match on raw text.
    /// </summary>
    public enum SocialErrorCode
    {
        None = 0,
        Unknown,
        NotSignedIn,
        NetworkUnavailable,
        Timeout,
        ServiceUnavailable,
        RateLimited,
        PermissionDenied,
        NotInClan,
        AlreadyInClan,
        AlreadyMember,
        ClanNotFound,
        ClanFull,
        ClanIsPrivate,
        InvalidName,
        InvalidTag,
        TagTaken,
        InviteNotFound,
        InviteExpired,
        InviteExists,
        RequestNotFound,
        NotAMember,
        OwnerMustTransfer,
        EmptyMessage,
        DuplicateMessage,
        InvalidScore,
        LeaderboardUnavailable,
        InvalidRequest,
        Cancelled,
    }
}
