// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;
using osu.Framework.Graphics.UserInterface;
using osu.Game.Graphics.UserInterface;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Localisation;
using osu.Framework.Screens;
using osu.Game.Graphics.Containers;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Chat;
using osu.Game.Resources.Localisation.Web;
using osu.Game.Localisation;
using osu.Game.Online.Metadata;
using osu.Game.Online.Multiplayer;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Users.Drawables;
using osuTK;

namespace osu.Game.Users
{
    public abstract partial class UserPanel : OsuClickableContainer, IHasContextMenu, IFilterable
    {
        public readonly APIUser User;

        /// <summary>
        /// Perform an action in addition to showing the user's profile.
        /// This should be used to perform auxiliary tasks and not as a primary action for clicking a user panel (to maintain a consistent UX).
        /// </summary>
        public new Action? Action;

        protected Action ViewProfile { get; private set; } = null!;

        protected Drawable Background { get; private set; } = null!;

        protected UserPanel(APIUser user)
            : base(HoverSampleSet.Button)
        {
            Debug.Write(user);
            ArgumentNullException.ThrowIfNull(user);

            User = user;
        }

        [Resolved]
        private UserProfileOverlay? profileOverlay { get; set; }

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        [Resolved]
        private ChannelManager? channelManager { get; set; }

        [Resolved]
        private ChatOverlay? chatOverlay { get; set; }

        [Resolved]
        private IDialogOverlay? dialogOverlay { get; set; }

        [Resolved]
        protected OverlayColourProvider? ColourProvider { get; private set; }

        [Resolved]
        private IPerformFromScreenRunner? performer { get; set; }

        [Resolved]
        protected OsuColour Colours { get; private set; } = null!;

        [Resolved]
        private MultiplayerClient? multiplayerClient { get; set; }

        [Resolved]
        private MetadataClient? metadataClient { get; set; }

        [BackgroundDependencyLoader]
        private void load()
        {
            Masking = true;

            Add(new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourProvider?.Background5 ?? Colours.Gray1
            });

            var background = CreateBackground();
            if (background != null)
                Add(background);

            Add(CreateLayout());

            base.Action = ViewProfile = () =>
            {
                Action?.Invoke();
                profileOverlay?.ShowUser(User);
            };
        }

        // TODO: this whole api is messy. half these Create methods are expected to by the implementation and half are implictly called.

        protected abstract Drawable CreateLayout();

        /// <summary>
        /// Panel background container. Can be null if a panel doesn't want a background under it's layout
        /// </summary>
        protected virtual Drawable? CreateBackground() => Background = new UserCoverBackground
        {
            RelativeSizeAxes = Axes.Both,
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            User = User
        };

        protected OsuSpriteText CreateUsername() => new OsuSpriteText
        {
            Font = OsuFont.GetFont(size: 16, weight: FontWeight.Bold),
            Shadow = false,
            Text = User.Username,
        };

        protected UpdateableAvatar CreateAvatar() => new UpdateableAvatar(User, false);

        protected UpdateableFlag CreateFlag() => new UpdateableFlag(User.CountryCode)
        {
            Size = new Vector2(36, 26),
            Action = Action,
        };

        protected Drawable CreateTeamLogo() => new UpdateableTeamFlag(User.Team)
        {
            Size = new Vector2(52, 26),
        };

        public MenuItem[] ContextMenuItems
        {
            get
            {
                List<MenuItem> items = new List<MenuItem>
                {
                    new OsuMenuItem(ContextMenuStrings.ViewProfile, MenuItemType.Highlighted, ViewProfile)
                };

                if (User.Equals(api.LocalUser.Value))
                    return items.ToArray();

                items.Add(new OsuMenuItem(UsersStrings.CardSendMessage, MenuItemType.Standard, () =>
                {
                    channelManager?.OpenPrivateChannel(User);
                    chatOverlay?.Show();
                }));

                items.Add(!isUserBlocked()
                    ? new OsuMenuItem(UsersStrings.BlocksButtonBlock, MenuItemType.Destructive, () => dialogOverlay?.Push(ConfirmBlockActionDialog.Block(User)))
                    : new OsuMenuItem(UsersStrings.BlocksButtonUnblock, MenuItemType.Standard, () => dialogOverlay?.Push(ConfirmBlockActionDialog.Unblock(User))));

                if (isUserOnline())
                {
                    items.Add(new OsuMenuItem(ContextMenuStrings.SpectatePlayer, MenuItemType.Standard, () =>
                    {
                        if (isUserOnline())
                            performer?.PerformFromScreen(s => s.Push(new SoloSpectatorScreen(User)));
                    }));

                    if (canInviteUser())
                    {
                        items.Add(new OsuMenuItem(ContextMenuStrings.InvitePlayer, MenuItemType.Standard, () =>
                        {
                            if (canInviteUser())
                                multiplayerClient!.InvitePlayer(User.Id);
                        }));
                    }
                }

                return items.ToArray();

                bool isUserOnline() => metadataClient?.GetPresence(User.OnlineID) != null;
                bool canInviteUser() => isUserOnline() && multiplayerClient?.Room?.Users.All(u => u.UserID != User.Id) == true;
                bool isUserBlocked() => api.Blocks.Any(b => b.TargetID == User.OnlineID);
            }
        }

        public IEnumerable<LocalisableString> FilterTerms => [User.Username];

        public bool MatchingFilter
        {
            set
            {
                if (value)
                    Show();
                else
                    Hide();
            }
        }

        public bool FilteringActive { get; set; }
    }
}
