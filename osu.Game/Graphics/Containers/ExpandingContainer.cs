// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Events;
using osu.Framework.Threading;

namespace osu.Game.Graphics.Containers
{
    /// <summary>
    /// Represents a <see cref="Container"/> with the ability to expand/contract on hover.
    /// </summary>
    public partial class ExpandingContainer : Container, IExpandingContainer
    {
        public const double TRANSITION_DURATION = 500;

        private readonly float contractedWidth;
        private readonly float expandedWidth;

        public BindableBool Expanded { get; } = new BindableBool();

        /// <summary>
        /// Delay before the container switches to expanded state from hover.
        /// </summary>
        protected virtual double HoverExpansionDelay => 0;

        protected virtual bool ExpandOnHover => true;

        protected override Container<Drawable> Content => FillFlow;

        protected FillFlowContainer FillFlow { get; }

        protected ExpandingContainer(float contractedWidth, float expandedWidth)
        {
            this.contractedWidth = contractedWidth;
            this.expandedWidth = expandedWidth;

            RelativeSizeAxes = Axes.Y;
            Width = contractedWidth;

            InternalChild = CreateScrollContainer().With(s =>
            {
                s.RelativeSizeAxes = Axes.Both;
                s.ScrollbarVisible = false;
            }).WithChild(
                FillFlow = new FillFlowContainer
                {
                    Origin = Anchor.CentreLeft,
                    Anchor = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                }
            );
        }

        protected virtual OsuScrollContainer CreateScrollContainer() => new OsuScrollContainer();

        private ScheduledDelegate? hoverExpandEvent;

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Expanded.BindValueChanged(v =>
            {
                this.ResizeWidthTo(v.NewValue ? expandedWidth : contractedWidth, TRANSITION_DURATION, Easing.OutQuint);
            }, true);
        }

        protected override bool OnHover(HoverEvent e)
        {
            updateHoverExpansion();
            return true;
        }

        protected override bool OnMouseMove(MouseMoveEvent e)
        {
            updateHoverExpansion();
            return base.OnMouseMove(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            if (hoverExpandEvent != null)
            {
                hoverExpandEvent?.Cancel();
                hoverExpandEvent = null;

                Expanded.Value = false;
                return;
            }

            base.OnHoverLost(e);
        }

        private void updateHoverExpansion()
        {
            if (!ExpandOnHover)
                return;

            hoverExpandEvent?.Cancel();

            if (IsHovered && !Expanded.Value)
                hoverExpandEvent = Scheduler.AddDelayed(() => Expanded.Value = true, HoverExpansionDelay);
        }
    }
}
