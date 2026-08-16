using System;
using System.Threading;
using System.Threading.Tasks;
using ClanSystem.CoreData;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

namespace ClanSystem.Services
{
    /// <summary>
    /// Anonymous sign-in through Unity Authentication, with an optional profile name.
    /// Distinct profiles give distinct player ids on one machine, which is what makes the
    /// multi-client demo workflow possible without a second device.
    /// </summary>
    public class UgsAuthenticationGateway : IAuthenticationGateway
    {
        private bool _isInitialized;

        public bool IsSignedIn => AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn;

        public string PlayerId => IsSignedIn ? AuthenticationService.Instance.PlayerId : string.Empty;

        public string PlayerName => IsSignedIn ? AuthenticationService.Instance.PlayerName : string.Empty;

        public event Action<string> PlayerNameChanged;

        public async Task<SocialResult> InitializeAsync(string profileName, CancellationToken cancellationToken)
        {
            try
            {
                if (UnityServices.State == ServicesInitializationState.Initialized)
                {
                    if (!string.IsNullOrEmpty(profileName) && AuthenticationService.Instance != null && !AuthenticationService.Instance.IsSignedIn)
                    {
                        AuthenticationService.Instance.SwitchProfile(profileName);
                    }

                    HookNameEvent();
                    _isInitialized = true;
                    return SocialResult.Success();
                }

                InitializationOptions options = new InitializationOptions();
                if (!string.IsNullOrEmpty(profileName))
                {
                    options.SetProfile(profileName);
                }

                await UnityServices.InitializeAsync(options);
                cancellationToken.ThrowIfCancellationRequested();

                HookNameEvent();
                _isInitialized = true;
                return SocialResult.Success();
            }
            catch (OperationCanceledException)
            {
                return SocialResult.Failure(SocialErrorCode.Cancelled, SocialErrorMapper.Describe(SocialErrorCode.Cancelled));
            }
            catch (Exception exception)
            {
                Debug.LogError($"[ClanSystem] Unity Services initialization failed: {exception.Message}");
                SocialErrorCode code = SocialErrorMapper.FromException(exception);
                return SocialResult.Failure(code, "Could not reach Unity Gaming Services. Check the project link in Project Settings > Services.");
            }
        }

        public async Task<SocialResult> SignInAsync(CancellationToken cancellationToken)
        {
            if (!_isInitialized)
            {
                return SocialResult.Failure(SocialErrorCode.ServiceUnavailable, "Services are not initialized yet.");
            }

            try
            {
                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // A generated name is requested up front so every roster row and chat line has a
                // label even before the player customises it.
                await AuthenticationService.Instance.GetPlayerNameAsync();
                return SocialResult.Success();
            }
            catch (OperationCanceledException)
            {
                return SocialResult.Failure(SocialErrorCode.Cancelled, SocialErrorMapper.Describe(SocialErrorCode.Cancelled));
            }
            catch (AuthenticationException exception)
            {
                Debug.LogError($"[ClanSystem] Sign-in failed: {exception.Message}");
                return SocialResult.Failure(SocialErrorCode.NotSignedIn, "Sign-in failed. Check your connection and try again.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[ClanSystem] Sign-in failed: {exception.Message}");
                SocialErrorCode code = SocialErrorMapper.FromException(exception);
                return SocialResult.Failure(code, SocialErrorMapper.Describe(code));
            }
        }

        public async Task<SocialResult<string>> SetPlayerNameAsync(string name, CancellationToken cancellationToken)
        {
            if (!IsSignedIn)
            {
                return SocialResult<string>.Failure(SocialErrorCode.NotSignedIn, SocialErrorMapper.Describe(SocialErrorCode.NotSignedIn));
            }

            string trimmed = name != null ? name.Trim() : string.Empty;
            if (trimmed.Length < 3)
            {
                return SocialResult<string>.Failure(SocialErrorCode.InvalidName, "Names need at least 3 characters.");
            }

            try
            {
                string applied = await AuthenticationService.Instance.UpdatePlayerNameAsync(trimmed);
                cancellationToken.ThrowIfCancellationRequested();
                return SocialResult<string>.Success(applied);
            }
            catch (OperationCanceledException)
            {
                return SocialResult<string>.Failure(SocialErrorCode.Cancelled, SocialErrorMapper.Describe(SocialErrorCode.Cancelled));
            }
            catch (Exception exception)
            {
                SocialErrorCode code = SocialErrorMapper.FromException(exception);
                return SocialResult<string>.Failure(code, "That name was rejected. Try a different one.");
            }
        }

        public void SignOut()
        {
            if (AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn)
            {
                AuthenticationService.Instance.SignOut();
            }
        }

        private void HookNameEvent()
        {
            if (AuthenticationService.Instance == null)
            {
                return;
            }

            AuthenticationService.Instance.PlayerNameChanged -= PlayerNameChangedCallback;
            AuthenticationService.Instance.PlayerNameChanged += PlayerNameChangedCallback;
        }

        private void PlayerNameChangedCallback(string name)
        {
            PlayerNameChanged?.Invoke(name);
        }
    }
}
