using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Composition root for the demo scene. Builds the gateways and the coordinator, drives the
    /// sign-in gate, and tears everything down on destroy so pending polls do not outlive the scene.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class SocialBootstrap : MonoBehaviour
    {
        [SerializeField] private ClanSystemConfig _config;
        [SerializeField] private bool _signInOnStart = false;
        [Tooltip("Authentication profile used when signing in. Different profiles produce different player ids on the same machine.")]
        [SerializeField] private string _defaultProfile = "default";

        private UIDocument _document;
        private SocialCoordinator _coordinator;
        private SocialWindowController _window;

        private VisualElement _signInPanel;
        private VisualElement _mainPanel;
        private TextField _profileField;
        private Button _signInButton;
        private Label _signInStatus;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
        }

        private void OnEnable()
        {
            VisualElement root = _document.rootVisualElement;

            _signInPanel = root.Q<VisualElement>("signin-panel");
            _mainPanel = root.Q<VisualElement>("main-panel");
            _profileField = root.Q<TextField>("profile-field");
            _signInButton = root.Q<Button>("signin-button");
            _signInStatus = root.Q<Label>("signin-status");

            if (_config == null)
            {
                ShowSignInStatus("No ClanSystemConfig assigned on the SocialBootstrap component.");
                _signInButton.SetEnabled(false);
                return;
            }

            _profileField.SetValueWithoutNotify(_defaultProfile);
            _signInButton.clicked += OnButtonClick_SignIn;

            if (_signInOnStart)
            {
                OnButtonClick_SignIn();
            }
        }

        private void OnDestroy()
        {
            _window?.Dispose();
            _coordinator?.Dispose();
        }

        private void OnButtonClick_SignIn()
        {
            _signInButton.SetEnabled(false);
            ShowSignInStatus(string.Empty);
            _ = SignInAsync();
        }

        private async System.Threading.Tasks.Task SignInAsync()
        {
            string profile = SanitizeProfile(_profileField.value);

            UgsAuthenticationGateway auth = new UgsAuthenticationGateway();
            UgsFriendsGateway friends = new UgsFriendsGateway();
            CloudCodeSocialBackend backend = new CloudCodeSocialBackend(_config);

            // Every Vivox token is minted by Cloud Code against the player's real clan membership.
            CloudCodeVivoxTokenProvider tokenProvider = new CloudCodeVivoxTokenProvider(_config);
            VivoxCommunicationService communication = new VivoxCommunicationService(_config, tokenProvider);

            _coordinator = new SocialCoordinator(_config, auth, friends, backend, communication);

            SocialResult result = await _coordinator.StartAsync(profile);
            if (!result.IsSuccess)
            {
                ShowSignInStatus(result.Message);
                _signInButton.SetEnabled(true);
                _coordinator.Dispose();
                _coordinator = null;
                return;
            }

            _signInPanel.style.display = DisplayStyle.None;
            _mainPanel.style.display = DisplayStyle.Flex;

            _window = new SocialWindowController(_document.rootVisualElement, _coordinator);
            _window.RefreshHeader();
        }

        private void ShowSignInStatus(string message)
        {
            if (_signInStatus != null)
            {
                _signInStatus.text = message ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(message))
            {
                Debug.LogWarning($"[ClanSystem] {message}");
            }
        }

        /// <summary>
        /// Profile names are restricted by the Authentication SDK to a small character set, so the
        /// free-text field is normalised before it reaches sign-in.
        /// </summary>
        private static string SanitizeProfile(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "default";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                bool isAllowed = char.IsLetterOrDigit(character) || character == '-' || character == '_';
                if (isAllowed)
                {
                    builder.Append(character);
                }
            }

            string cleaned = builder.ToString();
            if (cleaned.Length == 0)
            {
                return "default";
            }

            return cleaned.Length > 30 ? cleaned.Substring(0, 30) : cleaned;
        }
    }
}
