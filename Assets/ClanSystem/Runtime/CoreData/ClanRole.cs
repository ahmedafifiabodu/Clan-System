namespace ClanSystem.CoreData
{
    /// <summary>
    /// Rank of a player inside a clan. Ordered so that a higher value outranks a lower one;
    /// the client uses this only to hide controls - the server re-checks every permission.
    /// </summary>
    public enum ClanRole
    {
        None = 0,
        Member = 1,
        Officer = 2,
        Owner = 3,
    }
}
