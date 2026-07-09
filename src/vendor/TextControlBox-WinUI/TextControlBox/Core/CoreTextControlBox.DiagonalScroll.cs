using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Interactions;
using Microsoft.UI.Xaml.Hosting;

namespace TextControlBoxNS.Core;

/// <summary>
/// Diagonal (two-axis) precision-touchpad panning — Option B of
/// PLANS/EDITOR_DIAGONAL_SCROLL_REWRITE_PLAN.md.
///
/// <para>A composition <see cref="InteractionTracker"/> + <see cref="VisualInteractionSource"/> on the
/// selection canvas reads the raw 2-D precision-touchpad manipulation delta — the thing the single-axis
/// wheel stream physically cannot express — and drives the Phase-1 pixel <c>IScrollOffsetSource</c>, after
/// which the existing viewport-pinned slice redraw repaints. The source is configured
/// <see cref="VisualInteractionSourceRedirectionMode.CapableTouchpadOnly"/> so it captures ONLY the
/// precision-touchpad pan; the mouse wheel, Shift+wheel, and Ctrl+wheel zoom keep flowing through the
/// existing <c>PointerActionsManager.PointerWheelAction</c> untouched, and mouse click/drag selection is
/// unaffected. The two <c>ScrollBar</c> primitives stay in sync because the tracker writes through the same
/// offset source they back.</para>
/// </summary>
internal sealed partial class CoreTextControlBox : IInteractionTrackerOwner
{
    private InteractionTracker _scrollTracker;
    private VisualInteractionSource _scrollInteractionSource;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _scrollTrackerTimer;
    private bool _scrollTrackerReady;
    private bool _scrollTrackerInteracting;
    private float _lastTrackerX;
    private float _lastTrackerY;

    /// <summary>Wires the tracker to the selection canvas' composition visual. Called from the control's
    /// <c>Loaded</c> event (the visual + size are available by then). Idempotent and best-effort — a
    /// composition failure just leaves the editor on wheel-only scrolling.</summary>
    private void SetupDiagonalScroll()
    {
        if (_scrollTrackerReady)
            return;

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(canvasSelection);
            Compositor compositor = visual.Compositor;

            _scrollTracker = InteractionTracker.CreateWithOwner(compositor, this);
            _scrollTracker.MinPosition = Vector3.Zero;
            _scrollTracker.MaxPosition = Vector3.Zero; // updated from content extent by the sync timer

            _scrollInteractionSource = VisualInteractionSource.Create(visual);
            // Capture ONLY precision-touchpad manipulation (leave wheel + zoom to the existing handler).
            _scrollInteractionSource.ManipulationRedirectionMode = VisualInteractionSourceRedirectionMode.CapableTouchpadOnly;
            _scrollInteractionSource.PositionXSourceMode = InteractionSourceMode.EnabledWithoutInertia;
            _scrollInteractionSource.PositionYSourceMode = InteractionSourceMode.EnabledWithoutInertia;
            // Don't chain past the editor to an ancestor scroller.
            _scrollInteractionSource.PositionXChainingMode = InteractionChainingMode.Never;
            _scrollInteractionSource.PositionYChainingMode = InteractionChainingMode.Never;
            _scrollTracker.InteractionSources.Add(_scrollInteractionSource);

            _scrollTrackerTimer = DispatcherQueue.CreateTimer();
            _scrollTrackerTimer.Interval = TimeSpan.FromMilliseconds(50);
            _scrollTrackerTimer.Tick += (_, _) => SyncScrollTracker();
            _scrollTrackerTimer.Start();

            _scrollTrackerReady = true;
            TextControlBoxDiagnostics.Verbose("TextControlBox.Scroll", "Diagonal-scroll InteractionTracker ready.");
        }
        catch (Exception ex)
        {
            TextControlBoxDiagnostics.Error("TextControlBox.Scroll", "SetupDiagonalScroll failed", ex);
        }
    }

    /// <summary>Keeps the tracker's scrollable range sized to the content extent, and (when the user isn't
    /// actively panning) re-syncs the tracker position to programmatic scrolls (caret-follow, go-to-line,
    /// page keys, wheel) so the next touchpad pan starts from the right place.</summary>
    private void SyncScrollTracker()
    {
        if (!_scrollTrackerReady || scrollManager?.OffsetSource is not { } src)
            return;

        try
        {
            var max = new Vector3(
                (float)Math.Max(0, src.HorizontalExtent - src.ViewportWidth),
                (float)Math.Max(0, src.VerticalExtent - src.ViewportHeight),
                0);
            if (_scrollTracker.MaxPosition != max)
                _scrollTracker.MaxPosition = max;

            if (!_scrollTrackerInteracting)
            {
                float srcX = (float)src.HorizontalOffset;
                float srcY = (float)src.VerticalOffset;
                if (Math.Abs(srcX - _lastTrackerX) > 0.5f || Math.Abs(srcY - _lastTrackerY) > 0.5f)
                {
                    _lastTrackerX = srcX;
                    _lastTrackerY = srcY;
                    _scrollTracker.TryUpdatePosition(new Vector3(srcX, srcY, 0));
                }
            }
        }
        catch (Exception ex)
        {
            TextControlBoxDiagnostics.Error("TextControlBox.Scroll", "SyncScrollTracker failed", ex);
        }
    }

    /// <summary>Stops the sync timer and disposes the composition tracker + interaction source. Called from
    /// <c>Unload()</c> so an editor instance never leaks a forever-running 50 ms <c>DispatcherQueueTimer</c>
    /// or its composition objects. Idempotent and best-effort.</summary>
    private void TeardownDiagonalScroll()
    {
        if (!_scrollTrackerReady && _scrollTrackerTimer is null && _scrollTracker is null)
            return;

        try
        {
            if (_scrollTrackerTimer is not null)
            {
                _scrollTrackerTimer.Stop();
                _scrollTrackerTimer = null;
            }

            if (_scrollTracker is not null && _scrollInteractionSource is not null)
                _scrollTracker.InteractionSources.RemoveAll();

            _scrollInteractionSource?.Dispose();
            _scrollInteractionSource = null;

            _scrollTracker?.Dispose();
            _scrollTracker = null;
        }
        catch (Exception ex)
        {
            TextControlBoxDiagnostics.Error("TextControlBox.Scroll", "TeardownDiagonalScroll failed", ex);
        }
        finally
        {
            _scrollTrackerReady = false;
            _scrollTrackerInteracting = false;
        }
    }

    // ---- IInteractionTrackerOwner -----------------------------------------------------------------
    // ValuesChanged is raised on the UI thread (WinUI's compositor is UI-thread-affined), so it is safe
    // to touch the offset source + request a redraw directly.
    public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        _lastTrackerX = args.Position.X;
        _lastTrackerY = args.Position.Y;

        if (scrollManager?.OffsetSource is { } src)
        {
            src.HorizontalOffset = args.Position.X;
            src.VerticalOffset = args.Position.Y;
            canvasUpdateManager.UpdateAll();
        }
    }

    public void InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args)
        => _scrollTrackerInteracting = true;

    public void IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args)
        => _scrollTrackerInteracting = false;

    public void InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args) { }

    public void CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args) { }

    public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args) { }
}
