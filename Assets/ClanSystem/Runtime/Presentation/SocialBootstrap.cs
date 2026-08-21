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
        /// <summary>Spinner tick. 16ms is one frame at 60fps, which is what makes it look smooth.</summary>
        private const int _spinnerIntervalMs = 16;

        /// <summary>Degrees per tick. 6 at 60fps is one turn per second.</summary>
        private const float _spinnerDegreesPerTick = 6f;

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

        private VisualElement _signInProgress;
        private VisualElement _signInSpinner;
        private Label _signInStage;
        private IVisualElementScheduledItem _spinnerHandle;
        private float _spinnerAngle;

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
            _signInProgress = root.Q<VisualElement>("signin-progress");
            _signInSpinner = root.Q<VisualElement>("signin-spinner");
            _signInStage = root.Q<Label>("signin-stage");

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
            _spinnerHandle?.Pause();
            _window?.Dispose();
            _coordinator?.Dispose();
        }

        private void OnButtonClick_SignIn()
        {
            _signInButton.SetEnabled(false);
            ShowSignInStatus(string.Empty);
            SetProgressVisible(true, "Starting Unity Gaming Services...");
            _ = SignInAsync();
        }

        /// <summary>
        /// Shows or hides the sign-in spinner. The rotation is driven from a scheduler tick rather
        /// than a stylesheet animation because UI Toolkit has no keyframes - only transitions, which
        /// cannot loop. The scheduler is paused while hidden so an idle sign-in screen costs nothing.
        /// </summary>
        private void SetProgressVisible(bool isVisible, string stage)
        {
            if (_signInProgress == null)
            {
                return;
            }

            _signInProgress.style.display = isVisible ? DisplayStyle.Flex : DisplayStyle.None;
            ShowSignInStage(stage);

            if (isVisible)
            {
                _spinnerHandle ??= _signInProgress.schedule.Execute(AdvanceSpinner).Every(_spinnerIntervalMs);
                _spinnerHandle.Resume();
            }
            else
            {
                _spinnerHandle?.Pause();
            }
        }

        private void AdvanceSpinner()
        {
            _spinnerAngle = (_spinnerAngle + _spinnerDegreesPerTick) % 360f;
            _signInSpinner.style.rotate = new StyleRotate(new Rotate(new Angle(_spinnerAngle, AngleUnit.Degree)));
        }

        private void ShowSignInStage(string stage)
        {
            if (_signInStage != null)
            {
                _signInStage.text = stage ?? string.Empty;
            }
        }

        private void StartupStageChangedCallback(string stage)
        {
            ShowSignInStage(stage);
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
            _coordinator.StartupStageChanged += StartupStageChangedCallback;

            SocialResult result = await _coordinator.StartAsync(profile);
            _coordinator.StartupStageChanged -= StartupStageChangedCallback;
            SetProgressVisible(false, string.Empty);

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

            // Only now: friends and voice report their failures as status messages, and the window
            // is what listens for those. Starting them any earlier would drop the message.
            _coordinator.StartBackgroundServices();
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
