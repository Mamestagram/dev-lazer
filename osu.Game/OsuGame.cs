// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Humanizer;
using JetBrains.Annotations;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Input.Handlers.Tablet;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Collections;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Extensions;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Game.Input;
using osu.Game.Input.Bindings;
using osu.Game.IO;
using osu.Game.Localisation;
using osu.Game.Online;
using osu.Game.Online.API.Requests;
using osu.Game.Online.Chat;
using osu.Game.Online.Leaderboards;
using osu.Game.Online.Rooms;
using osu.Game.Overlays;
using osu.Game.Overlays.BeatmapListing;
using osu.Game.Overlays.Mods;
using osu.Game.Overlays.Music;
using osu.Game.Overlays.Notifications;
using osu.Game.Overlays.OSD;
using osu.Game.Overlays.SkinEditor;
using osu.Game.Overlays.Toolbar;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens;
using osu.Game.Screens.Edit;
using osu.Game.Screens.Footer;
using osu.Game.Screens.Menu;
using osu.Game.Screens.OnlinePlay.DailyChallenge;
using osu.Game.Screens.OnlinePlay.Multiplayer;
using osu.Game.Screens.OnlinePlay.Playlists;
using osu.Game.Screens.Play;
using osu.Game.Screens.Ranking;
using osu.Game.Screens.Select;
using osu.Game.Screens.Select.Leaderboards;
using osu.Game.Seasonal;
using osu.Game.Skinning;
using osu.Game.Updater;
using osu.Game.Users;
using osu.Game.Utils;
using osuTK;
using osuTK.Graphics;
using Sentry;
using MatchType = osu.Game.Online.Rooms.MatchType;

namespace osu.Game
{
    /// <summary>
    /// The full osu! experience. Builds on top of <see cref="OsuGameBase"/> to add menus and binding logic
    /// for initial components that are generally retrieved via DI.
    /// </summary>
    [Cached(typeof(OsuGame))]
    public partial class OsuGame : OsuGameBase, IKeyBindingHandler<GlobalAction>, ILocalUserPlayInfo, IPerformFromScreenRunner, IOverlayManager, ILinkHandler
    {
#if DEBUG
        // Different port allows running release and debug builds alongside each other.
        public const string IPC_PIPE_NAME = "osu-lazer-debug";
#else
        public const string IPC_PIPE_NAME = "osu-lazer";
#endif

        /// <summary>
        /// The amount of global offset to apply when a left/right anchored overlay is displayed (ie. settings or notifications).
        /// </summary>
        protected const float SIDE_OVERLAY_OFFSET_RATIO = 0.05f;

        /// <summary>
        /// A common shear factor applied to most components of the game.
        /// </summary>
        public static readonly Vector2 SHEAR = new Vector2(0.2f, 0);

        /// <summary>
        /// For elements placed close to the screen edge, this is the margin to leave to the edge.
        /// </summary>
        public const float SCREEN_EDGE_MARGIN = 12f;

        private const double general_log_debounce = 60000;
        private const string tablet_log_prefix = @"[Tablet] ";

        public Toolbar Toolbar { get; private set; }

        private ChatOverlay chatOverlay;

        private ChannelManager channelManager;

        [NotNull]
        protected readonly NotificationOverlay Notifications = new NotificationOverlay();

        private BeatmapListingOverlay beatmapListing;

        private DashboardOverlay dashboard;

        private NewsOverlay news;

        private UserProfileOverlay userProfile;

        private BeatmapSetOverlay beatmapSetOverlay;

        private WikiOverlay wikiOverlay;

        private ChangelogOverlay changelogOverlay;

        private SkinEditorOverlay skinEditor;

        private Container overlayContent;

        private Container rightFloatingOverlayContent;

        private Container leftFloatingOverlayContent;

        private Container topMostOverlayContent;

        private Container footerBasedOverlayContent;

        protected ScalingContainer ScreenContainer { get; private set; }

        protected Container ScreenOffsetContainer { get; private set; }

        private Container overlayOffsetContainer;

        private OnScreenDisplay onScreenDisplay;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; }

        private DifficultyRecommender difficultyRecommender;

        [Cached]
        private readonly LegacyImportManager legacyImportManager = new LegacyImportManager();

        [Cached]
        private readonly ScreenshotManager screenshotManager = new ScreenshotManager();

        protected SentryLogger SentryLogger;

        public virtual StableStorage GetStorageForStableInstall() => null;

        private float toolbarOffset => (Toolbar?.Position.Y ?? 0) + (Toolbar?.DrawHeight ?? 0);

        private IdleTracker idleTracker;

        /// <summary>
        /// Whether the user is currently in an idle state.
        /// </summary>
        public IBindable<bool> IsIdle => idleTracker.IsIdle;

        /// <summary>
        /// Whether overlays should be able to be opened game-wide. Value is sourced from the current active screen.
        /// </summary>
        public readonly IBindable<OverlayActivation> OverlayActivationMode = new Bindable<OverlayActivation>();

        /// <summary>
        /// Whether the back button is currently displayed.
        /// </summary>
        private readonly IBindable<bool> backButtonVisibility = new BindableBool();

        IBindable<LocalUserPlayingState> ILocalUserPlayInfo.PlayingState => UserPlayingState;

        protected readonly Bindable<LocalUserPlayingState> UserPlayingState = new Bindable<LocalUserPlayingState>();

        protected OsuScreenStack ScreenStack;

        protected BackButton BackButton;
        protected ScreenFooter ScreenFooter;

        protected SettingsOverlay Settings;

        protected FirstRunSetupOverlay FirstRunOverlay { get; private set; }

        private FPSCounter fpsCounter;

        private VolumeOverlay volume;

        private OsuLogo osuLogo;

        private MainMenu menuScreen;

        [CanBeNull]
        private DevBuildBanner devBuildBanner;

        [CanBeNull]
        private IntroScreen introScreen;

        private Bindable<string> configRuleset;

        private Bindable<bool> applySafeAreaConsiderations;

        private Bindable<float> uiScale;

        private Bindable<UserActivity> configUserActivity;

        private Bindable<string> configSkin;

        private RealmDetachedBeatmapStore detachedBeatmapStore;

        private readonly string[] args;

        private readonly List<OsuFocusedOverlayContainer> focusedOverlays = new List<OsuFocusedOverlayContainer>();
        private readonly List<OverlayContainer> externalOverlays = new List<OverlayContainer>();

        private readonly List<OverlayContainer> visibleBlockingOverlays = new List<OverlayContainer>();

        /// <summary>
        /// Whether the game should be limited to only display officially licensed content.
        /// </summary>
        public virtual bool HideUnlicensedContent => false;

        private bool tabletLogNotifyOnWarning = true;
        private bool tabletLogNotifyOnError = true;
        private int generalLogRecentCount;

        public OsuGame(string[] args = null)
        {
            this.args = args;

            Logger.NewEntry += forwardGeneralLogToNotifications;
            Logger.NewEntry += forwardTabletLogToNotifications;

            Schedule(() =>
            {
                ITabletHandler tablet = Host.AvailableInputHandlers.OfType<ITabletHandler>().SingleOrDefault();
                tablet?.Tablet.BindValueChanged(_ =>
                {
                    tabletLogNotifyOnWarning = true;
                    tabletLogNotifyOnError = true;
                }, true);
            });
        }

        #region IOverlayManager

        IBindable<OverlayActivation> IOverlayManager.OverlayActivationMode => OverlayActivationMode;

        private void updateBlockingOverlayFade() =>
            ScreenContainer.FadeColour(visibleBlockingOverlays.Any() ? OsuColour.Gray(0.5f) : Color4.White, 500, Easing.OutQuint);

        IDisposable IOverlayManager.RegisterBlockingOverlay(OverlayContainer overlayContainer)
        {
            if (overlayContainer.Parent != null)
                throw new ArgumentException($@"Overlays registered via {nameof(IOverlayManager.RegisterBlockingOverlay)} should not be added to the scene graph.");

            if (externalOverlays.Contains(overlayContainer))
                throw new ArgumentException($@"{overlayContainer} has already been registered via {nameof(IOverlayManager.RegisterBlockingOverlay)} once.");

            externalOverlays.Add(overlayContainer);

            if (overlayContainer is ShearedOverlayContainer)
                footerBasedOverlayContent.Add(overlayContainer);
            else
                overlayContent.Add(overlayContainer);

            if (overlayContainer is OsuFocusedOverlayContainer focusedOverlayContainer)
                focusedOverlays.Add(focusedOverlayContainer);

            return new InvokeOnDisposal(() => unregisterBlockingOverlay(overlayContainer));
        }

        void IOverlayManager.ShowBlockingOverlay(OverlayContainer overlay)
        {
            if (!visibleBlockingOverlays.Contains(overlay))
                visibleBlockingOverlays.Add(overlay);
            updateBlockingOverlayFade();
        }

        void IOverlayManager.HideBlockingOverlay(OverlayContainer overlay) => Schedule(() =>
        {
            visibleBlockingOverlays.Remove(overlay);
            updateBlockingOverlayFade();
        });

        /// <summary>
        /// Unregisters a blocking <see cref="OverlayContainer"/> that was not created by <see cref="OsuGame"/> itself.
        /// </summary>
        private void unregisterBlockingOverlay(OverlayContainer overlayContainer) => Schedule(() =>
        {
            externalOverlays.Remove(overlayContainer);

            if (overlayContainer is OsuFocusedOverlayContainer focusedOverlayContainer)
                focusedOverlays.Remove(focusedOverlayContainer);

            overlayContainer.Expire();
        });

        #endregion

        /// <summary>
        /// Close all game-wide overlays.
        /// </summary>
        /// <param name="hideToolbar">Whether the toolbar should also be hidden.</param>
        public void CloseAllOverlays(bool hideToolbar = true)
        {
            foreach (var overlay in focusedOverlays)
                overlay.Hide();

            ScreenFooter.ActiveOverlay?.Hide();

            if (hideToolbar) Toolbar.Hide();
        }

        protected override UserInputManager CreateUserInputManager()
        {
            var userInputManager = base.CreateUserInputManager();
            (userInputManager as OsuUserInputManager)?.PlayingState.BindTo(UserPlayingState);
            return userInputManager;
        }

        private DependencyContainer dependencies;

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent) =>
            dependencies = new DependencyContainer(base.CreateChildDependencies(parent));

        private readonly List<string> dragDropFiles = new List<string>();
        private ScheduledDelegate dragDropImportSchedule;

        public override void SetupLogging(Storage gameStorage, Storage cacheStorage)
        {
            base.SetupLogging(gameStorage, cacheStorage);
            SentryLogger = new SentryLogger(this, cacheStorage);
        }

        public override void SetHost(GameHost host)
        {
            base.SetHost(host);

            if (host.Window != null)
            {
                host.Window.CursorState |= CursorState.Hidden;
                host.Window.DragDrop += onWindowDragDrop;
            }
        }

        private void onWindowDragDrop(string path)
        {
            // on macOS/iOS, URL associations are handled via SDL_DROPFILE events.
            if (path.StartsWith(OSU_PROTOCOL, StringComparison.Ordinal))
            {
                HandleLink(path);
                return;
            }

            lock (dragDropFiles)
            {
                dragDropFiles.Add(path);

                Logger.Log($@"Adding ""{Path.GetFileName(path)}"" for import");

                // File drag drop operations can potentially trigger hundreds or thousands of these calls on some platforms.
                // In order to avoid spawning multiple import tasks for a single drop operation, debounce a touch.
                dragDropImportSchedule?.Cancel();
                dragDropImportSchedule = Scheduler.AddDelayed(handlePendingDragDropImports, 100);
            }

            void handlePendingDragDropImports()
            {
                lock (dragDropFiles)
                {
                    Logger.Log($"Handling batch import of {dragDropFiles.Count} files");

                    string[] paths = dragDropFiles.ToArray();
                    dragDropFiles.Clear();

                    Task.Factory.StartNew(() => Import(paths), TaskCreationOptions.LongRunning);
                }
            }
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            SentryLogger.AttachUser(API.LocalUser);

            if (SeasonalUIConfig.ENABLED)
                dependencies.CacheAs(osuLogo = new OsuLogoChristmas { Alpha = 0 });
            else
                dependencies.CacheAs(osuLogo = new OsuLogo { Alpha = 0 });

            // bind config int to database RulesetInfo
            configRuleset = LocalConfig.GetBindable<string>(OsuSetting.Ruleset);
            uiScale = LocalConfig.GetBindable<float>(OsuSetting.UIScale);

            var preferredRuleset = RulesetStore.GetRuleset(configRuleset.Value);

            try
            {
                Ruleset.Value = preferredRuleset ?? RulesetStore.AvailableRulesets.First();
            }
            catch (Exception e)
            {
                // on startup, a ruleset may be selected which has compatibility issues.
                Logger.Error(e, $@"Failed to switch to preferred ruleset {preferredRuleset}.");
                Ruleset.Value = RulesetStore.AvailableRulesets.First();
            }

            Ruleset.ValueChanged += r => configRuleset.Value = r.NewValue.ShortName;

            configUserActivity = SessionStatics.GetBindable<UserActivity>(Static.UserOnlineActivity);

            configSkin = LocalConfig.GetBindable<string>(OsuSetting.Skin);

            // Transfer skin from config to realm instance once on startup.
            SkinManager.SetSkinFromConfiguration(configSkin.Value);

            // Transfer any runtime changes back to configuration file.
            SkinManager.CurrentSkinInfo.ValueChanged += skin => configSkin.Value = skin.NewValue.ID.ToString();

            UserPlayingState.BindValueChanged(p =>
            {
                BeatmapManager.PauseImports = p.NewValue != LocalUserPlayingState.NotPlaying;
                SkinManager.PauseImports = p.NewValue != LocalUserPlayingState.NotPlaying;
                ScoreManager.PauseImports = p.NewValue != LocalUserPlayingState.NotPlaying;
            }, true);

            IsActive.BindValueChanged(active => updateActiveState(active.NewValue), true);

            Audio.AddAdjustment(AdjustableProperty.Volume, inactiveVolumeFade);

            SelectedMods.BindValueChanged(modsChanged);
            Beatmap.BindValueChanged(beatmapChanged, true);
            configUserActivity.BindValueChanged(_ => updateWindowTitle());

            applySafeAreaConsiderations = LocalConfig.GetBindable<bool>(OsuSetting.SafeAreaConsiderations);
            applySafeAreaConsiderations.BindValueChanged(apply => SafeAreaContainer.SafeAreaOverrideEdges = apply.NewValue ? SafeAreaOverrideEdges : Edges.All, true);
        }

        private ExternalLinkOpener externalLinkOpener;

        /// <summary>
        /// Handle an arbitrary URL. Displays via in-game overlays where possible.
        /// This can be called from a non-thread-safe non-game-loaded state.
        /// </summary>
        /// <param name="url">The URL to load.</param>
        public void HandleLink(string url) => HandleLink(MessageFormatter.GetLinkDetails(url));

        /// <summary>
        /// Handle a specific <see cref="LinkDetails"/>.
        /// This can be called from a non-thread-safe non-game-loaded state.
        /// </summary>
        /// <param name="link">The link to load.</param>
        public void HandleLink(LinkDetails link) => Schedule(() =>
        {
            string argString = link.Argument.ToString() ?? string.Empty;

            switch (link.Action)
            {
                case LinkAction.OpenBeatmap:
                    // TODO: proper query params handling
                    if (int.TryParse(argString.Contains('?') ? argString.Split('?')[0] : argString, out int beatmapId))
                        ShowBeatmap(beatmapId);
                    break;

                case LinkAction.OpenBeatmapSet:
                    if (int.TryParse(argString, out int setId))
                        ShowBeatmapSet(setId);
                    break;

                case LinkAction.OpenChannel:
                    ShowChannel(argString);
                    break;

                case LinkAction.SearchBeatmapSet:
                    if (link.Argument is LocalisableString localisable)
                        SearchBeatmapSet(Localisation.GetLocalisedString(localisable));
                    else
                        SearchBeatmapSet(argString);

                    break;

                case LinkAction.FilterBeatmapSetGenre:
                    FilterBeatmapSetGenre((SearchGenre)link.Argument);
                    break;

                case LinkAction.FilterBeatmapSetLanguage:
                    FilterBeatmapSetLanguage((SearchLanguage)link.Argument);
                    break;

                case LinkAction.OpenEditorTimestamp:
                    HandleTimestamp(argString);
                    break;

                case LinkAction.Spectate:
                    waitForReady(() => Notifications, _ => Notifications.Post(new SimpleNotification
                    {
                        Text = NotificationsStrings.LinkTypeNotSupported,
                        Icon = FontAwesome.Solid.LifeRing,
                    }));
                    break;

                case LinkAction.External:
                    OpenUrlExternally(argString);
                    break;

                case LinkAction.OpenUserProfile:
                    ShowUser((IUser)link.Argument);
                    break;

                case LinkAction.OpenWiki:
                    ShowWiki(argString);
                    break;

                case LinkAction.OpenChangelog:
                    if (string.IsNullOrEmpty(argString))
                        ShowChangelogListing();
                    else
                    {
                        string[] changelogArgs = argString.Split("/");
                        ShowChangelogBuild($"{changelogArgs[1]}-{changelogArgs[0]}");
                    }

                    break;

                case LinkAction.JoinRoom:
                    if (long.TryParse(argString, out long roomId))
                        JoinRoom(roomId);
                    break;

                default:
                    throw new NotImplementedException($"This {nameof(LinkAction)} ({link.Action.ToString()}) is missing an associated action.");
            }
        });

        public void CopyToClipboard(string value) => waitForReady(() => onScreenDisplay, _ =>
        {
            dependencies.Get<Clipboard>().SetText(value);
            onScreenDisplay.Display(new CopiedToClipboardToast());
        });

        public void OpenUrlExternally(string url, LinkWarnMode warnMode = LinkWarnMode.Default) => waitForReady(() => externalLinkOpener, _ => externalLinkOpener.OpenUrlExternally(url, warnMode));

        /// <summary>
        /// Open a specific channel in chat.
        /// </summary>
        /// <param name="channel">The channel to display.</param>
        public void ShowChannel(string channel) => waitForReady(() => channelManager, _ =>
        {
            try
            {
                channelManager.OpenChannel(channel);
            }
            catch (ChannelNotFoundException)
            {
                Logger.Log($"The requested channel \"{channel}\" does not exist");
            }
        });

        /// <summary>
        /// Show a beatmap set as an overlay.
        /// </summary>
        /// <param name="setId">The set to display.</param>
        public void ShowBeatmapSet(int setId) => waitForReady(() => beatmapSetOverlay, _ => beatmapSetOverlay.FetchAndShowBeatmapSet(setId));

        /// <summary>
        /// Show a user's profile as an overlay.
        /// </summary>
        /// <param name="user">The user to display.</param>
        public void ShowUser(IUser user) => waitForReady(() => userProfile, _ => userProfile.ShowUser(user));

        /// <summary>
        /// Show a beatmap's set as an overlay, displaying the given beatmap.
        /// </summary>
        /// <param name="beatmapId">The beatmap to show.</param>
        public void ShowBeatmap(int beatmapId) => waitForReady(() => beatmapSetOverlay, _ => beatmapSetOverlay.FetchAndShowBeatmap(beatmapId));

        /// <summary>
        /// Shows the beatmap listing overlay, with the given <paramref name="query"/> in the search box.
        /// </summary>
        /// <param name="query">The query to search for.</param>
        public void SearchBeatmapSet(string query) => waitForReady(() => beatmapListing, _ => beatmapListing.ShowWithSearch(query));

        public void FilterBeatmapSetGenre(SearchGenre genre) => waitForReady(() => beatmapListing, _ => beatmapListing.ShowWithGenreFilter(genre));

        public void FilterBeatmapSetLanguage(SearchLanguage language) => waitForReady(() => beatmapListing, _ => beatmapListing.ShowWithLanguageFilter(language));

        /// <summary>
        /// Show a wiki's page as an overlay
        /// </summary>
        /// <param name="path">The wiki page to show</param>
        public void ShowWiki(string path) => waitForReady(() => wikiOverlay, _ => wikiOverlay.ShowPage(path));

        /// <summary>
        /// Show changelog listing overlay
        /// </summary>
        public void ShowChangelogListing() => waitForReady(() => changelogOverlay, _ => changelogOverlay.ShowListing());

        /// <summary>
        /// Show changelog's build as an overlay
        /// </summary>
        /// <param name="version">The build version, including stream suffix.</param>
        public void ShowChangelogBuild(string version) => waitForReady(() => changelogOverlay, _ => changelogOverlay.ShowBuild(version));

        /// <summary>
        /// Joins a multiplayer or playlists room with the given <paramref name="id"/>.
        /// </summary>
        public void JoinRoom(long id)
        {
            var request = new GetRoomRequest(id);
            request.Success += room =>
            {
                switch (room.Type)
                {
                    case MatchType.Playlists:
                        PresentPlaylist(room);
                        break;

                    default:
                        PresentMultiplayerMatch(room, string.Empty);
                        break;
                }
            };
            API.Queue(request);
        }

        /// <summary>
        /// Seeks to the provided <paramref name="timestamp"/> if the editor is currently open.
        /// Can also select objects as indicated by the <paramref name="timestamp"/> (depends on ruleset implementation).
        /// </summary>
        public void HandleTimestamp(string timestamp)
        {
            if (ScreenStack.CurrentScreen is not Editor editor)
            {
                Schedule(() => Notifications.Post(new SimpleErrorNotification
                {
                    Icon = FontAwesome.Solid.ExclamationTriangle,
                    Text = EditorStrings.MustBeInEditorToHandleLinks
                }));
                return;
            }

            editor.HandleTimestamp(timestamp, notifyOnError: true);
        }

        /// <summary>
        /// Present a skin select immediately.
        /// </summary>
        /// <param name="skin">The skin to select.</param>
        public void PresentSkin(SkinInfo skin)
        {
            var databasedSkin = SkinManager.Query(s => s.ID == skin.ID);

            if (databasedSkin == null)
            {
                Logger.Log("The requested skin could not be loaded.", LoggingTarget.Information);
                return;
            }

            SkinManager.CurrentSkinInfo.Value = databasedSkin;
        }

        /// <summary>
        /// Present a beatmap at song select immediately.
        /// The user should have already requested this interactively.
        /// </summary>
        /// <param name="beatmap">The beatmap to select.</param>
        /// <param name="difficultyCriteria">Optional predicate used to narrow the set of difficulties to select from when presenting.</param>
        /// <remarks>
        /// Among items satisfying the predicate, the order of preference is:
        /// <list type="bullet">
        /// <item>beatmap with recommended difficulty, as provided by <see cref="DifficultyRecommender"/>,</item>
        /// <item>first beatmap from the current ruleset,</item>
        /// <item>first beatmap from any ruleset.</item>
        /// </list>
        /// </remarks>
        public void PresentBeatmap(IBeatmapSetInfo beatmap, Predicate<BeatmapInfo> difficultyCriteria = null)
        {
            Logger.Log($"Beginning {nameof(PresentBeatmap)} with beatmap {beatmap}");
            Live<BeatmapSetInfo> databasedSet = null;

            if (beatmap.OnlineID > 0)
                databasedSet = BeatmapManager.QueryBeatmapSet(s => s.OnlineID == beatmap.OnlineID && !s.DeletePending);

            if (beatmap is BeatmapSetInfo localBeatmap)
                databasedSet ??= BeatmapManager.QueryBeatmapSet(s => s.Hash == localBeatmap.Hash && !s.DeletePending);

            if (databasedSet == null)
            {
                Logger.Log("The requested beatmap could not be loaded.", LoggingTarget.Information);
                return;
            }

            var detachedSet = databasedSet.PerformRead(s => s.Detach());

            if (detachedSet.DeletePending)
            {
                Logger.Log("The requested beatmap has since been deleted.", LoggingTarget.Information);
                return;
            }

            PerformFromScreen(screen =>
            {
                // Find beatmaps that match our predicate.
                var beatmaps = detachedSet.Beatmaps.Where(b => difficultyCriteria?.Invoke(b) ?? true).ToList();

                // Use all beatmaps if predicate matched nothing
                if (beatmaps.Count == 0)
                    beatmaps = detachedSet.Beatmaps.ToList();

                // Prefer recommended beatmap if recommendations are available, else fallback to a sane selection.
                var selection = difficultyRecommender.GetRecommendedBeatmap(beatmaps)
                                ?? beatmaps.FirstOrDefault(b => b.Ruleset.Equals(Ruleset.Value))
                                ?? beatmaps.First();

                if (screen is IHandlePresentBeatmap presentableScreen)
                {
                    presentableScreen.PresentBeatmap(BeatmapManager.GetWorkingBeatmap(selection), selection.Ruleset);
                }
                else
                {
                    // Don't change the local ruleset if the user is on another ruleset and is showing converted beatmaps at song select.
                    // Eventually we probably want to check whether conversion is actually possible for the current ruleset.
                    bool requiresRulesetSwitch = !selection.Ruleset.Equals(Ruleset.Value)
                                                 && (selection.Ruleset.OnlineID > 0 || !LocalConfig.Get<bool>(OsuSetting.ShowConvertedBeatmaps));

                    if (requiresRulesetSwitch)
                    {
                        Ruleset.Value = selection.Ruleset;
                        Beatmap.Value = BeatmapManager.GetWorkingBeatmap(selection);

                        Logger.Log($"Completing {nameof(PresentBeatmap)} with beatmap {beatmap} ruleset {selection.Ruleset}");
                    }
                    else
                    {
                        Beatmap.Value = BeatmapManager.GetWorkingBeatmap(selection);

                        Logger.Log($"Completing {nameof(PresentBeatmap)} with beatmap {beatmap} (maintaining ruleset)");
                    }
                }
            }, validScreens: new[]
            {
                typeof(SongSelect), typeof(Screens.SelectV2.SongSelect), typeof(IHandlePresentBeatmap)
            });
        }

        /// <summary>
        /// Join a multiplayer match immediately.
        /// </summary>
        /// <param name="room">The room to join.</param>
        /// <param name="password">The password to join the room, if any is given.</param>
        public void PresentMultiplayerMatch(Room room, string password)
        {
            if (room.HasEnded)
            {
                // TODO: Eventually it should be possible to display ended multiplayer rooms in game too,
                // but it generally will require turning off the entirety of communication with spectator server which is currently embedded into multiplayer screens.
                Notifications.Post(new SimpleNotification
                {
                    Text = NotificationsStrings.MultiplayerRoomEnded,
                    Activated = () =>
                    {
                        OpenUrlExternally($@"/multiplayer/rooms/{room.RoomID}");
                        return true;
                    }
                });
                return;
            }

            PerformFromScreen(screen =>
            {
                if (!(screen is Multiplayer multiplayer))
                    screen.Push(multiplayer = new Multiplayer());

                multiplayer.Join(room, password);
            });
            // TODO: We should really be able to use `validScreens: new[] { typeof(Multiplayer) }` here
            // but `PerformFromScreen` doesn't understand nested stacks.
        }

        /// <summary>
        /// Join a playlist immediately.
        /// </summary>
        /// <param name="room">The playlist to join.</param>
        public void PresentPlaylist(Room room)
        {
            PerformFromScreen(screen =>
            {
                if (!(screen is Playlists playlists))
                    screen.Push(playlists = new Playlists());

                playlists.Join(room);
            });
            // TODO: We should really be able to use `validScreens: new[] { typeof(Playlists) }` here
            // but `PerformFromScreen` doesn't understand nested stacks.
        }

        /// <summary>
        /// Present a score's replay immediately.
        /// The user should have already requested this interactively.
        /// </summary>
        public void PresentScore(IScoreInfo score, ScorePresentType presentType = ScorePresentType.Results)
        {
            Logger.Log($"Beginning {nameof(PresentScore)} with score {score}");

            Score databasedScore;

            try
            {
                databasedScore = ScoreManager.GetScore(score);
            }
            catch (LegacyScoreDecoder.BeatmapNotFoundException notFound)
            {
                Logger.Log("The replay cannot be played because the beatmap is missing.", LoggingTarget.Information);

                var req = new GetBeatmapRequest(new BeatmapInfo { MD5Hash = notFound.Hash });
                req.Success += res => Notifications.Post(new MissingBeatmapNotification(res, notFound.Hash, null));
                API.Queue(req);

                return;
            }

            if (databasedScore == null) return;

            if (databasedScore.Replay == null)
            {
                Logger.Log("The loaded score has no replay data.", LoggingTarget.Information, LogLevel.Important);
                return;
            }

            var databasedBeatmap = databasedScore.ScoreInfo.BeatmapInfo;
            Debug.Assert(databasedBeatmap != null);

            // This should be able to be performed from song select always, but that is disabled for now
            // due to the weird decoupled ruleset logic (which can cause a crash in certain filter scenarios).
            //
            // As a special case, if the beatmap and ruleset already match, allow immediately displaying the score from song select.
            // This is guaranteed to not crash, and feels better from a user's perspective (ie. if they are clicking a score in the
            // song select leaderboard).
            // Similar exemptions are made here for daily challenge where it is guaranteed that beatmap and ruleset match.
            // `OnlinePlayScreen` is excluded because when resuming back to it,
            // `RoomSubScreen` changes the global beatmap to the next playlist item on resume,
            // which may not match the score, and thus crash.
            IEnumerable<Type> validScreens =
                Beatmap.Value.BeatmapInfo.Equals(databasedBeatmap) && Ruleset.Value.Equals(databasedScore.ScoreInfo.Ruleset)
                    ? new[] { typeof(SongSelect), typeof(Screens.SelectV2.SongSelect), typeof(DailyChallenge) }
                    : Array.Empty<Type>();

            PerformFromScreen(screen =>
            {
                Logger.Log($"{nameof(PresentScore)} updating beatmap ({databasedBeatmap}) and ruleset ({databasedScore.ScoreInfo.Ruleset}) to match score");

                // some screens (mostly online) disable the ruleset/beatmap bindable.
                // attempting to set the ruleset/beatmap in that state will crash.
                // however, the `validScreens` pre-check above should ensure that we actually never come from one of those screens
                // while simultaneously having mismatched ruleset/beatmap.
                // therefore this is just a safety against touching the possibly-disabled bindables if we don't actually have to touch them.
                // if it ever fails, then this probably *should* crash anyhow (so that we can fix it).
                if (!Ruleset.Value.Equals(databasedScore.ScoreInfo.Ruleset))
                    Ruleset.Value = databasedScore.ScoreInfo.Ruleset;

                if (!Beatmap.Value.BeatmapInfo.Equals(databasedBeatmap))
                    Beatmap.Value = BeatmapManager.GetWorkingBeatmap(databasedBeatmap);

                var currentLeaderboard = LeaderboardManager.CurrentCriteria;

                bool leaderboardBeatmapMatches = currentLeaderboard != null && databasedBeatmap.Equals(currentLeaderboard.Beatmap);
                bool leaderboardRulesetMatches = currentLeaderboard != null && databasedScore.ScoreInfo.Ruleset.Equals(currentLeaderboard.Ruleset);

                if (!leaderboardBeatmapMatches || !leaderboardRulesetMatches)
                {
                    var newLeaderboard = currentLeaderboard != null
                        ? currentLeaderboard with { Beatmap = databasedBeatmap, Ruleset = databasedScore.ScoreInfo.Ruleset }
                        : new LeaderboardCriteria(databasedBeatmap, databasedScore.ScoreInfo.Ruleset, BeatmapLeaderboardScope.Global, null);
                    LeaderboardManager.FetchWithCriteria(newLeaderboard);
                }

                switch (presentType)
                {
                    case ScorePresentType.Gameplay:
                        screen.Push(new ReplayPlayerLoader(databasedScore));
                        break;

                    case ScorePresentType.Results:
                        screen.Push(new SoloResultsScreen(databasedScore.ScoreInfo));
                        break;
                }
            }, validScreens: validScreens);
        }

        public override Task Import(ImportTask[] imports, ImportParameters parameters = default)
        {
            // encapsulate task as we don't want to begin the import process until in a ready state.

            // ReSharper disable once AsyncVoidLambda
            // TODO: This is bad because `new Task` doesn't have a Func<Task?> override.
            // Only used for android imports and a bit of a mess. Probably needs rethinking overall.
            var importTask = new Task(async () => await base.Import(imports, parameters).ConfigureAwait(false));

            waitForReady(() => this, _ => importTask.Start());

            return importTask;
        }

        protected virtual Loader CreateLoader() => new Loader();

        protected virtual UpdateManager CreateUpdateManager() => new UpdateManager();

        /// <summary>
        /// Adjust the globally applied <see cref="DrawSizePreservingFillContainer.TargetDrawSize"/> in every <see cref="ScalingContainer"/>.
        /// Useful for changing how the game handles different aspect ratios.
        /// </summary>
        public virtual Vector2 ScalingContainerTargetDrawSize { get; } = new Vector2(1024, 768);

        protected override Container CreateScalingContainer() => new ScalingContainer(ScalingMode.Everything);

        #region Beatmap progression

        private void beatmapChanged(ValueChangedEvent<WorkingBeatmap> beatmap)
        {
            beatmap.OldValue?.CancelAsyncLoad();
            beatmap.NewValue?.BeginAsyncLoad();
            updateWindowTitle();
        }

        private void updateWindowTitle()
        {
            if (Host.Window == null)
                return;

            string newTitle;

            switch (configUserActivity.Value)
            {
                default:
                    newTitle = Name;
                    break;

                case UserActivity.InGame:
                case UserActivity.TestingBeatmap:
                case UserActivity.WatchingReplay:
                    newTitle = $"{Name} - {Beatmap.Value.BeatmapInfo.GetDisplayTitleRomanisable(true, false)}";
                    break;

                case UserActivity.EditingBeatmap:
                    newTitle = $"{Name} - {Beatmap.Value.BeatmapInfo.Path ?? "new beatmap"}";
                    break;
            }

            if (newTitle != Host.Window.Title)
                Host.Window.Title = newTitle;
        }

        private void modsChanged(ValueChangedEvent<IReadOnlyList<Mod>> mods)
        {
            // a lease may be taken on the mods bindable, at which point we can't really ensure valid mods.
            if (SelectedMods.Disabled)
                return;

            if (!ModUtils.CheckValidForGameplay(mods.NewValue, out var invalid))
            {
                // ensure we always have a valid set of mods.
                SelectedMods.Value = mods.NewValue.Except(invalid).ToArray();
            }
        }

        #endregion

        private PerformFromMenuRunner performFromMainMenuTask;

        public void PerformFromScreen(Action<IScreen> action, IEnumerable<Type> validScreens = null)
        {
            performFromMainMenuTask?.Cancel();
            Add(performFromMainMenuTask = new PerformFromMenuRunner(action, validScreens, () => ScreenStack.CurrentScreen));
        }

        public override void AttemptExit()
        {
            // The main menu exit implementation gives the user a chance to interrupt the exit process if needed.
            PerformFromScreen(menu => menu.Exit(), new[] { typeof(MainMenu) });
        }

        /// <summary>
        /// Wait for the game (and target component) to become loaded and then run an action.
        /// </summary>
        /// <param name="retrieveInstance">A function to retrieve a (potentially not-yet-constructed) target instance.</param>
        /// <param name="action">The action to perform on the instance when load is confirmed.</param>
        /// <typeparam name="T">The type of the target instance.</typeparam>
        private void waitForReady<T>(Func<T> retrieveInstance, Action<T> action)
            where T : Drawable
        {
            var instance = retrieveInstance();

            if (ScreenStack == null || ScreenStack.CurrentScreen is StartupScreen || instance?.IsLoaded != true)
                Schedule(() => waitForReady(retrieveInstance, action));
            else
                action(instance);
        }

        protected override void Dispose(bool isDisposing)
        {
            // Without this, tests may deadlock due to cancellation token not becoming cancelled before disposal.
            // To reproduce, run `TestSceneButtonSystemNavigation` ensuring `TestConstructor` runs before `TestFastShortcutKeys`.
            detachedBeatmapStore?.Dispose();

            base.Dispose(isDisposing);

            SentryLogger.Dispose();

            if (Host?.Window != null)
                Host.Window.DragDrop -= onWindowDragDrop;

            Logger.NewEntry -= forwardGeneralLogToNotifications;
            Logger.NewEntry -= forwardTabletLogToNotifications;
        }

        protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults()
        {
            return new Dictionary<FrameworkSetting, object>
            {
                // General expectation that osu! starts in fullscreen by default (also gives the most predictable performance).
                // However, macOS is bound to have issues when using exclusive fullscreen as it takes full control away from OS, therefore borderless is default there.
                { FrameworkSetting.WindowMode, RuntimeInfo.OS == RuntimeInfo.Platform.macOS ? WindowMode.Borderless : WindowMode.Fullscreen },
                { FrameworkSetting.VolumeUniversal, 0.6 },
                { FrameworkSetting.VolumeMusic, 0.6 },
                { FrameworkSetting.VolumeEffect, 0.6 },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            if (RuntimeInfo.EntryAssembly.GetCustomAttribute<OfficialBuildAttribute>() == null)
                Logger.Log(NotificationsStrings.NotOfficialBuild.ToString());

            // Make sure the release stream setting matches the build which was just run.
            if (Enum.TryParse<ReleaseStream>(Version.Split('-').Last(), true, out var releaseStream))
                LocalConfig.SetValue(OsuSetting.ReleaseStream, releaseStream);

            var languages = Enum.GetValues<Language>();

            var mappings = languages.Select(language =>
            {
#if DEBUG
                if (language == Language.debug)
                    return new LocaleMapping("debug", new DebugLocalisationStore());
#endif

                string cultureCode = language.ToCultureCode();

                try
                {
                    return new LocaleMapping(new ResourceManagerLocalisationStore(cultureCode));
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Could not load localisations for language \"{cultureCode}\"");
                    return null;
                }
            }).Where(m => m != null);

            Localisation.AddLocaleMappings(mappings);

            // The next time this is updated is in UpdateAfterChildren, which occurs too late and results
            // in the cursor being shown for a few frames during the intro.
            // This prevents the cursor from showing until we have a screen with CursorVisible = true
            GlobalCursorDisplay.ShowCursor = menuScreen?.CursorVisible ?? false;

            // todo: all archive managers should be able to be looped here.
            SkinManager.PostNotification = n => Notifications.Post(n);
            SkinManager.PresentImport = items => PresentSkin(items.First().Value);

            BeatmapManager.PostNotification = n => Notifications.Post(n);
            BeatmapManager.PresentImport = items => PresentBeatmap(items.First().Value);

            BeatmapDownloader.PostNotification = n => Notifications.Post(n);
            ScoreDownloader.PostNotification = n => Notifications.Post(n);

            ScoreManager.PostNotification = n => Notifications.Post(n);
            ScoreManager.PresentImport = items => PresentScore(items.First().Value);

            MultiplayerClient.PostNotification = n => Notifications.Post(n);
            MultiplayerClient.PresentMatch = PresentMultiplayerMatch;

            ScreenFooter.BackReceptor backReceptor;

            dependencies.CacheAs(idleTracker = new GameIdleTracker(6000));

            var sessionIdleTracker = new GameIdleTracker(300000);
            sessionIdleTracker.IsIdle.BindValueChanged(idle =>
            {
                if (idle.NewValue)
                    SessionStatics.ResetAfterInactivity();
            });

            Add(sessionIdleTracker);

            Container logoContainer;

            AddRange(new Drawable[]
            {
                ScreenOffsetContainer = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        ScreenContainer = new ScalingContainer(ScalingMode.ExcludeOverlays)
                        {
                            RelativeSizeAxes = Axes.Both,
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Children = new Drawable[]
                            {
                                backReceptor = new ScreenFooter.BackReceptor(),
                                ScreenStack = new OsuScreenStack { RelativeSizeAxes = Axes.Both },
                                BackButton = new BackButton(backReceptor)
                                {
                                    Anchor = Anchor.BottomLeft,
                                    Origin = Anchor.BottomLeft,
                                    Action = handleBackButton,
                                },
                                logoContainer = new Container { RelativeSizeAxes = Axes.Both },
                                // TODO: what is this? why is this?
                                // TODO: this is being screen scaled even though it's probably AN OVERLAY.
                                footerBasedOverlayContent = new Container
                                {
                                    Depth = -1,
                                    RelativeSizeAxes = Axes.Both,
                                },
                                new PopoverContainer
                                {
                                    Depth = -1,
                                    RelativeSizeAxes = Axes.Both,
                                    Child = ScreenFooter = new ScreenFooter(backReceptor)
                                    {
                                        // TODO: this is really really weird and should not exist.
                                        RequestLogoInFront = inFront => ScreenContainer.ChangeChildDepth(logoContainer, inFront ? float.MinValue : 0),
                                        BackButtonPressed = handleBackButton
                                    },
                                },
                            }
                        },
                    }
                },
                overlayOffsetContainer = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        overlayContent = new Container { RelativeSizeAxes = Axes.Both },
                        leftFloatingOverlayContent = new Container { RelativeSizeAxes = Axes.Both },
                        rightFloatingOverlayContent = new Container { RelativeSizeAxes = Axes.Both },
                    }
                },
                topMostOverlayContent = new Container { RelativeSizeAxes = Axes.Both },
                idleTracker,
                new ConfineMouseTracker()
            });

            dependencies.Cache(ScreenFooter);

            ScreenStack.ScreenPushed += screenPushed;
            ScreenStack.ScreenExited += screenExited;

            loadComponentSingleFile(fpsCounter = new FPSCounter
            {
                Anchor = Anchor.BottomRight,
                Origin = Anchor.BottomRight,
                Margin = new MarginPadding(5),
            }, topMostOverlayContent.Add);

            if (!IsDeployedBuild)
                loadComponentSingleFile(devBuildBanner = new DevBuildBanner(), ScreenContainer.Add);

            loadComponentSingleFile(osuLogo, _ =>
            {
                osuLogo.SetupDefaultContainer(logoContainer);

                // Loader has to be created after the logo has finished loading as Loader performs logo transformations on entering.
                ScreenStack.Push(CreateLoader().With(l => l.RelativeSizeAxes = Axes.Both));
            });

            LocalUserStatisticsProvider statisticsProvider;

            loadComponentSingleFile(statisticsProvider = new LocalUserStatisticsProvider(), Add, true);
            loadComponentSingleFile(difficultyRecommender = new DifficultyRecommender(statisticsProvider), Add, true);
            loadComponentSingleFile(new UserStatisticsWatcher(statisticsProvider), Add, true);
            loadComponentSingleFile(Toolbar = new Toolbar
            {
                OnHome = delegate
                {
                    CloseAllOverlays(false);

                    if (menuScreen?.GetChildScreen() != null)
                        menuScreen.MakeCurrent();
                },
            }, topMostOverlayContent.Add);

            loadComponentSingleFile(volume = new VolumeOverlay(), leftFloatingOverlayContent.Add, true);

            onScreenDisplay = new OnScreenDisplay();

            onScreenDisplay.BeginTracking(this, frameworkConfig);
            onScreenDisplay.BeginTracking(this, LocalConfig);

            loadComponentSingleFile(onScreenDisplay, Add, true);

            loadComponentSingleFile<INotificationOverlay>(Notifications.With(d =>
            {
                d.Anchor = Anchor.TopRight;
                d.Origin = Anchor.TopRight;
            }), rightFloatingOverlayContent.Add, true);

            loadComponentSingleFile(legacyImportManager, Add);

            loadComponentSingleFile(screenshotManager, Add);

            // dependency on notification overlay, dependent by settings overlay
            loadComponentSingleFile(CreateUpdateManager(), Add, true);

            // overlay elements
            loadComponentSingleFile(FirstRunOverlay = new FirstRunSetupOverlay(), footerBasedOverlayContent.Add, true);
            loadComponentSingleFile(new ManageCollectionsDialog(), overlayContent.Add, true);
            loadComponentSingleFile(beatmapListing = new BeatmapListingOverlay(), overlayContent.Add, true);
            loadComponentSingleFile(dashboard = new DashboardOverlay(), overlayContent.Add, true);
            loadComponentSingleFile(news = new NewsOverlay(), overlayContent.Add, true);
            var rankingsOverlay = loadComponentSingleFile(new RankingsOverlay(), overlayContent.Add, true);
            loadComponentSingleFile(channelManager = new ChannelManager(API), Add, true);
            loadComponentSingleFile(chatOverlay = new ChatOverlay(), overlayContent.Add, true);
            loadComponentSingleFile(new MessageNotifier(), Add, true);
            loadComponentSingleFile(Settings = new SettingsOverlay(), leftFloatingOverlayContent.Add, true);
            loadComponentSingleFile(changelogOverlay = new ChangelogOverlay(), overlayContent.Add, true);
            loadComponentSingleFile(userProfile = new UserProfileOverlay(), overlayContent.Add, true);
            loadComponentSingleFile(beatmapSetOverlay = new BeatmapSetOverlay(), overlayContent.Add, true);
            loadComponentSingleFile(wikiOverlay = new WikiOverlay(), overlayContent.Add, true);
            loadComponentSingleFile(skinEditor = new SkinEditorOverlay(ScreenContainer), overlayContent.Add, true);

            loadComponentSingleFile(new LoginOverlay
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
            }, rightFloatingOverlayContent.Add, true);

            loadComponentSingleFile(new NowPlayingOverlay
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
            }, rightFloatingOverlayContent.Add, true);

            loadComponentSingleFile(new AccountCreationOverlay(), topMostOverlayContent.Add, true);
            loadComponentSingleFile<IDialogOverlay>(new DialogOverlay(), topMostOverlayContent.Add, true);
            loadComponentSingleFile(new MedalOverlay(), topMostOverlayContent.Add);

            loadComponentSingleFile(new BackgroundDataStoreProcessor(), Add);
            loadComponentSingleFile<BeatmapStore>(detachedBeatmapStore = new RealmDetachedBeatmapStore(), Add, true);

            Add(externalLinkOpener = new ExternalLinkOpener());
            Add(new MusicKeyBindingHandler());
            Add(new OnlineStatusNotifier(() => ScreenStack.CurrentScreen));
            Add(new FriendPresenceNotifier());

            // side overlays which cancel each other.
            var singleDisplaySideOverlays = new OverlayContainer[] { Settings, Notifications, FirstRunOverlay };

            foreach (var overlay in singleDisplaySideOverlays)
            {
                overlay.State.ValueChanged += state =>
                {
                    if (state.NewValue == Visibility.Hidden) return;

                    singleDisplaySideOverlays.Where(o => o != overlay).ForEach(o => o.Hide());
                };
            }

            // eventually informational overlays should be displayed in a stack, but for now let's only allow one to stay open at a time.
            var informationalOverlays = new OverlayContainer[] { beatmapSetOverlay, userProfile };

            foreach (var overlay in informationalOverlays)
            {
                overlay.State.ValueChanged += state =>
                {
                    if (state.NewValue != Visibility.Hidden)
                        showOverlayAboveOthers(overlay, informationalOverlays);
                };
            }

            // ensure only one of these overlays are open at once.
            var singleDisplayOverlays = new OverlayContainer[] { chatOverlay, news, dashboard, beatmapListing, changelogOverlay, rankingsOverlay, wikiOverlay };

            foreach (var overlay in singleDisplayOverlays)
            {
                overlay.State.ValueChanged += state =>
                {
                    // informational overlays should be dismissed on a show or hide of a full overlay.
                    informationalOverlays.ForEach(o => o.Hide());

                    if (state.NewValue != Visibility.Hidden)
                        showOverlayAboveOthers(overlay, singleDisplayOverlays);
                };
            }

            OverlayActivationMode.ValueChanged += mode =>
            {
                if (mode.NewValue != OverlayActivation.All) CloseAllOverlays();
            };

            backButtonVisibility.ValueChanged += visible =>
            {
                if (visible.NewValue)
                    BackButton.Show();
                else
                    BackButton.Hide();
            };

            // Importantly, this should be run after binding PostNotification to the import handlers so they can present the import after game startup.
            handleStartupImport();
        }

        private void handleBackButton()
        {
            // TODO: this is SUPER SUPER bad.
            // It can potentially exit the wrong screen if screens are not loaded yet.
            // ScreenFooter / ScreenBackButton should be aware of which screen it is currently being handled by.
            if (!(ScreenStack.CurrentScreen is IOsuScreen currentScreen)) return;

            if (!((Drawable)currentScreen).IsLoaded || (currentScreen.AllowUserExit && !currentScreen.OnBackButton())) ScreenStack.Exit();
        }

        private void handleStartupImport()
        {
            if (args?.Length > 0)
            {
                string[] paths = args.Where(a => !a.StartsWith('-')).ToArray();

                if (paths.Length > 0)
                {
                    string firstPath = paths.First();

                    if (firstPath.StartsWith(OSU_PROTOCOL, StringComparison.Ordinal))
                    {
                        HandleLink(firstPath);
                    }
                    else
                    {
                        Task.Run(() => Import(paths));
                    }
                }
            }
        }

        private void showOverlayAboveOthers(OverlayContainer overlay, OverlayContainer[] otherOverlays)
        {
            otherOverlays.Where(o => o != overlay).ForEach(o => o.Hide());

            Settings.Hide();
            Notifications.Hide();

            // Partially visible so leave it at the current depth.
            if (overlay.IsPresent)
                return;

            // Show above all other overlays.
            if (overlay.IsLoaded)
                overlayContent.ChangeChildDepth(overlay, (float)-Clock.CurrentTime);
            else
                overlay.Depth = (float)-Clock.CurrentTime;
        }

        private void forwardGeneralLogToNotifications(LogEntry entry)
        {
            if (entry.Level < LogLevel.Important || entry.Target > LoggingTarget.Database || entry.Target == null) return;

            if (entry.Exception is SentryOnlyDiagnosticsException)
                return;

            const int short_term_display_limit = 3;

            if (generalLogRecentCount < short_term_display_limit)
            {
                Schedule(() => Notifications.Post(new SimpleErrorNotification
                {
                    Icon = entry.Level == LogLevel.Important ? FontAwesome.Solid.ExclamationCircle : FontAwesome.Solid.Bomb,
                    Text = entry.Message.Truncate(256) + (entry.Exception != null && IsDeployedBuild ? "\n\nThis error has been automatically reported to the devs." : string.Empty),
                }));
            }
            else if (generalLogRecentCount == short_term_display_limit)
            {
                string logFile = Logger.GetLogger(entry.Target.Value).Filename;

                Schedule(() => Notifications.Post(new SimpleNotification
                {
                    Icon = FontAwesome.Solid.EllipsisH,
                    Text = NotificationsStrings.SubsequentMessagesLogged,
                    Activated = () =>
                    {
                        Logger.Storage.PresentFileExternally(logFile);
                        return true;
                    }
                }));
            }

            Interlocked.Increment(ref generalLogRecentCount);
            Scheduler.AddDelayed(() => Interlocked.Decrement(ref generalLogRecentCount), general_log_debounce);
        }

        private void forwardTabletLogToNotifications(LogEntry entry)
        {
            if (entry.Level < LogLevel.Important || entry.Target != LoggingTarget.Input || !entry.Message.StartsWith(tablet_log_prefix, StringComparison.OrdinalIgnoreCase))
                return;

            string message = entry.Message.Replace(tablet_log_prefix, string.Empty);

            if (entry.Level == LogLevel.Error)
            {
                if (!tabletLogNotifyOnError)
                    return;

                tabletLogNotifyOnError = false;

                Schedule(() =>
                {
                    Notifications.Post(new SimpleNotification
                    {
                        Text = NotificationsStrings.TabletSupportDisabledDueToError(message),
                        Icon = FontAwesome.Solid.PenSquare,
                        IconColour = Colours.RedDark,
                    });

                    // We only have one tablet handler currently.
                    // The loop here is weakly guarding against a future where more than one is added.
                    // If this is ever the case, this logic needs adjustment as it should probably only
                    // disable the relevant tablet handler rather than all.
                    foreach (var tabletHandler in Host.AvailableInputHandlers.OfType<ITabletHandler>())
                        tabletHandler.Enabled.Value = false;
                });
            }
            else if (tabletLogNotifyOnWarning)
            {
                Schedule(() => Notifications.Post(new SimpleNotification
                {
                    Text = NotificationsStrings.EncounteredTabletWarning,
                    Icon = FontAwesome.Solid.PenSquare,
                    IconColour = Colours.YellowDark,
                    Activated = () =>
                    {
                        OpenUrlExternally("https://opentabletdriver.net/Tablets", LinkWarnMode.NeverWarn);
                        return true;
                    }
                }));

                tabletLogNotifyOnWarning = false;
            }
        }

        private Task asyncLoadStream;

        /// <summary>
        /// Queues loading the provided component in sequential fashion.
        /// This operation is limited to a single thread to avoid saturating all cores.
        /// </summary>
        /// <param name="component">The component to load.</param>
        /// <param name="loadCompleteAction">An action to invoke on load completion (generally to add the component to the hierarchy).</param>
        /// <param name="cache">Whether to cache the component as type <typeparamref name="T"/> into the game dependencies before any scheduling.</param>
        private T loadComponentSingleFile<T>(T component, Action<Drawable> loadCompleteAction, bool cache = false)
            where T : class
        {
            if (cache)
                dependencies.CacheAs(component);

            var drawableComponent = component as Drawable ?? throw new ArgumentException($"Component must be a {nameof(Drawable)}", nameof(component));

            if (component is OsuFocusedOverlayContainer overlay)
                focusedOverlays.Add(overlay);

            // schedule is here to ensure that all component loads are done after LoadComplete is run (and thus all dependencies are cached).
            // with some better organisation of LoadComplete to do construction and dependency caching in one step, followed by calls to loadComponentSingleFile,
            // we could avoid the need for scheduling altogether.
            Schedule(() =>
            {
                var previousLoadStream = asyncLoadStream;

                // chain with existing load stream
                asyncLoadStream = Task.Run(async () =>
                {
                    if (previousLoadStream != null)
                        await previousLoadStream.ConfigureAwait(false);

                    try
                    {
                        Logger.Log($"Loading {component}...");

                        // Since this is running in a separate thread, it is possible for OsuGame to be disposed after LoadComponentAsync has been called
                        // throwing an exception. To avoid this, the call is scheduled on the update thread, which does not run if IsDisposed = true
                        Task task = null;
                        var del = new ScheduledDelegate(() => task = LoadComponentAsync(drawableComponent, loadCompleteAction));
                        Scheduler.Add(del);

                        // The delegate won't complete if OsuGame has been disposed in the meantime
                        while (!IsDisposed && !del.Completed)
                            await Task.Delay(10).ConfigureAwait(false);

                        // Either we're disposed or the load process has started successfully
                        if (IsDisposed)
                            return;

                        Debug.Assert(task != null);

                        await task.ConfigureAwait(false);

                        Logger.Log($"Loaded {component}!");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });
            });

            return component;
        }

        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            switch (e.Action)
            {
                case GlobalAction.DecreaseVolume:
                case GlobalAction.IncreaseVolume:
                    return volume.Adjust(e.Action);
            }

            // All actions below this point don't allow key repeat.
            if (e.Repeat)
                return false;

            // Wait until we're loaded at least to the intro before allowing various interactions.
            if (introScreen == null) return false;

            switch (e.Action)
            {
                case GlobalAction.ToggleMute:
                case GlobalAction.NextVolumeMeter:
                case GlobalAction.PreviousVolumeMeter:
                    return volume.Adjust(e.Action);

                case GlobalAction.ToggleFPSDisplay:
                    fpsCounter.ToggleVisibility();
                    return true;

                case GlobalAction.ToggleSkinEditor:
                    skinEditor.ToggleVisibility();
                    return true;

                case GlobalAction.ResetInputSettings:
                    Host.ResetInputHandlers();
                    frameworkConfig.GetBindable<ConfineMouseMode>(FrameworkSetting.ConfineMouseMode).SetDefault();
                    return true;

                case GlobalAction.ToggleGameplayMouseButtons:
                    var mouseDisableButtons = LocalConfig.GetBindable<bool>(OsuSetting.MouseDisableButtons);
                    mouseDisableButtons.Value = !mouseDisableButtons.Value;
                    return true;

                case GlobalAction.ToggleProfile:
                    if (userProfile.State.Value == Visibility.Visible)
                        userProfile.Hide();
                    else
                        ShowUser(API.LocalUser.Value);
                    return true;

                case GlobalAction.RandomSkin:
                    // Don't allow random skin selection while in the skin editor.
                    // This is mainly to stop many "osu! default (modified)" skins being created via the SkinManager.EnsureMutableSkin() path.
                    // If people want this to work we can potentially avoid selecting default skins when the editor is open, or allow a maximum of one mutable skin somehow.
                    if (skinEditor.State.Value == Visibility.Visible)
                        return false;

                    SkinManager.SelectRandomSkin();
                    return true;
            }

            return false;
        }

        public override bool OnPressed(KeyBindingPressEvent<PlatformAction> e)
        {
            const float adjustment_increment = 0.05f;

            switch (e.Action)
            {
                case PlatformAction.ZoomIn:
                    uiScale.Value += adjustment_increment;
                    return true;

                case PlatformAction.ZoomOut:
                    uiScale.Value -= adjustment_increment;
                    return true;

                case PlatformAction.ZoomDefault:
                    uiScale.SetDefault();
                    return true;
            }

            return base.OnPressed(e);
        }

        #region Inactive audio dimming

        private readonly BindableDouble inactiveVolumeFade = new BindableDouble();

        private void updateActiveState(bool isActive)
        {
            if (isActive)
                this.TransformBindableTo(inactiveVolumeFade, 1, 400, Easing.OutQuint);
            else
                this.TransformBindableTo(inactiveVolumeFade, LocalConfig.Get<double>(OsuSetting.VolumeInactive), 4000, Easing.OutQuint);
        }

        #endregion

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }

        protected override bool OnExiting()
        {
            if (ScreenStack.CurrentScreen is Loader)
                return false;

            if (introScreen?.DidLoadMenu == true && !(ScreenStack.CurrentScreen is IntroScreen))
            {
                Scheduler.Add(introScreen.MakeCurrent);
                return true;
            }

            return base.OnExiting();
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();

            ScreenOffsetContainer.Padding = new MarginPadding { Top = toolbarOffset };
            overlayOffsetContainer.Padding = new MarginPadding { Top = toolbarOffset };

            float horizontalOffset = 0f;

            // Content.ToLocalSpace() is used instead of this.ToLocalSpace() to correctly calculate the offset with scaling modes active.
            // Content is a child of a scaling container with ScalingMode.Everything set, while the game itself is never scaled.
            // this avoids a visible jump in the positioning of the screen offset container.
            if (Settings.IsLoaded && Settings.IsPresent)
                horizontalOffset += Content.ToLocalSpace(Settings.ScreenSpaceDrawQuad.TopRight).X * SIDE_OVERLAY_OFFSET_RATIO;
            if (Notifications.IsLoaded && Notifications.IsPresent)
                horizontalOffset += (Content.ToLocalSpace(Notifications.ScreenSpaceDrawQuad.TopLeft).X - Content.DrawWidth) * SIDE_OVERLAY_OFFSET_RATIO;

            ScreenOffsetContainer.X = horizontalOffset;
            overlayContent.X = horizontalOffset * 1.2f;

            GlobalCursorDisplay.ShowCursor = (ScreenStack.CurrentScreen as IOsuScreen)?.CursorVisible ?? false;
        }

        protected virtual void ScreenChanged([CanBeNull] IOsuScreen current, [CanBeNull] IOsuScreen newScreen)
        {
            SentrySdk.ConfigureScope(scope =>
            {
                scope.Contexts[@"screen stack"] = new
                {
                    Current = newScreen?.GetType().ReadableName(),
                    Previous = current?.GetType().ReadableName(),
                };

                scope.SetTag(@"screen", newScreen?.GetType().ReadableName() ?? @"none");
            });

            switch (current)
            {
                case Player player:
                    player.PlayingState.UnbindFrom(UserPlayingState);

                    // reset for sanity.
                    UserPlayingState.Value = LocalUserPlayingState.NotPlaying;
                    break;
            }

            switch (newScreen)
            {
                case IntroScreen intro:
                    introScreen = intro;
                    devBuildBanner?.Show();
                    break;

                case MainMenu menu:
                    menuScreen = menu;
                    devBuildBanner?.Show();
                    break;

                case Player player:
                    player.PlayingState.BindTo(UserPlayingState);
                    break;

                default:
                    devBuildBanner?.Hide();
                    break;
            }

            if (current != null)
            {
                backButtonVisibility.UnbindFrom(current.BackButtonVisibility);
                OverlayActivationMode.UnbindFrom(current.OverlayActivationMode);
                configUserActivity.UnbindFrom(current.Activity);
            }

            // Bind to new screen.
            if (newScreen != null)
            {
                OverlayActivationMode.BindTo(newScreen.OverlayActivationMode);
                configUserActivity.BindTo(newScreen.Activity);

                // Handle various configuration updates based on new screen settings.
                GlobalCursorDisplay.MenuCursor.HideCursorOnNonMouseInput = newScreen.HideMenuCursorOnNonMouseInput;

                if (newScreen.HideOverlaysOnEnter)
                    CloseAllOverlays();
                else
                    Toolbar.Show();

                var newOsuScreen = (OsuScreen)newScreen;

                if (newScreen.ShowFooter)
                {
                    // the legacy back button should never display while the new footer is in use, as it
                    // contains its own local back button.
                    ((BindableBool)backButtonVisibility).Value = false;

                    BackButton.Hide();
                    ScreenFooter.Show();

                    if (newOsuScreen.IsLoaded)
                        updateFooterButtons();
                    else
                    {
                        // ensure the current buttons are immediately disabled on screen change (so they can't be pressed).
                        ScreenFooter.SetButtons(Array.Empty<ScreenFooterButton>());

                        newOsuScreen.OnLoadComplete += _ => updateFooterButtons();
                    }

                    void updateFooterButtons()
                    {
                        var buttons = newScreen.CreateFooterButtons();

                        newOsuScreen.LoadComponentsAgainstScreenDependencies(buttons);

                        ScreenFooter.SetButtons(buttons);
                        ScreenFooter.Show();
                    }
                }
                else
                {
                    backButtonVisibility.BindTo(newScreen.BackButtonVisibility);

                    ScreenFooter.SetButtons(Array.Empty<ScreenFooterButton>());
                    ScreenFooter.Hide();
                }

                skinEditor.SetTarget(newOsuScreen);
            }
        }

        private void screenPushed(IScreen lastScreen, IScreen newScreen) => ScreenChanged((OsuScreen)lastScreen, (OsuScreen)newScreen);

        private void screenExited(IScreen lastScreen, IScreen newScreen)
        {
            ScreenChanged((OsuScreen)lastScreen, (OsuScreen)newScreen);

            if (newScreen == null)
                Exit();
        }
    }
}
