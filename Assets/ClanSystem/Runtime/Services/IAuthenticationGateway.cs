using System;
using System.Threading;
using System.Threading.Tasks;
using ClanSystem.CoreData;

namespace ClanSystem.Services
{
    /// <summary>
    /// Sign-in and player identity. Kept behind an interface so the social stack does not depend
    /// on the Authentication SDK directly and can be driven by a stub in tests.
    /// </summary>
    public interface IAuthenticationGateway
    {
        bool IsSignedIn { get; }
        string PlayerId { get; }
        string PlayerName { get; }

        event Action<string> PlayerNameChanged;

        Task<SocialResult> InitializeAsync(string profileName, CancellationToken cancellationToken);

        Task<SocialResult> SignInAsync(CancellationToken cancellationToken);

        Task<SocialResult<string>> SetPlayerNameAsync(string name, CancellationToken cancellationToken);

        void SignOut();
    }
}
