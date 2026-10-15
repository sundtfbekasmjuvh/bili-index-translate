using BiliIndexTranslate.Core.Geometry;

namespace BiliIndexTranslate.Core.Selection;

public enum PlacementSide { Above, Below }

/// <summary>Result of a placement computation; <see cref="WindowRect"/> includes the shadow insets.</summary>
public sealed record PopupPlacement(PxRect WindowRect, PxRect CardRect, PlacementSide Side, MonitorArea Monitor);

/// <summary>
/// Pure placement math. Everything is in physical pixels of the virtual desktop; the caller converts
/// gaps/margins using the monitor scale so that mixed-DPI setups keep the same visual distances.
/// </summary>
public static class PopupPlacer
{
    /// <summary>Logical gap between the text and the card (8–12 px requested; 10 chosen).</summary>
    public const double GapLogical = 10;

    /// <summary>Logical margin kept from the monitor work-area edges.</summary>
    public const double EdgeMarginLogical = 8;

    public static PopupPlacement Place(
        AnchorRect anchor,
        int cardWidth,
        int cardHeight,
        PxInsets insets,
        MonitorArea monitor,
        PlacementSide? preferredSide = null)
    {
        var work = monitor.WorkArea;
        var gap = monitor.Px(GapLogical);
        var margin = monitor.Px(EdgeMarginLogical);

        cardWidth = Math.Max(1, Math.Min(cardWidth, Math.Max(1, work.Width - 2 * margin)));
        cardHeight = Math.Max(1, cardHeight);

        // Horizontal: centre on the first (uppermost) selected line, clamp into the work area.
        var desiredLeft = anchor.FirstLine.CenterX - cardWidth / 2;
        var minLeft = work.Left + margin;
        var maxLeft = work.Right - margin - cardWidth;
        var left = maxLeft < minLeft ? minLeft : Math.Clamp(desiredLeft, minLeft, maxLeft);

        // Vertical: above the uppermost line when it fits, otherwise below the lowest line.
        var aboveTop = anchor.Union.Top - gap - cardHeight;
        var belowTop = anchor.Union.Bottom + gap;
        var fitsAbove = aboveTop >= work.Top + margin;
        var fitsBelow = belowTop + cardHeight <= work.Bottom - margin;

        PlacementSide side;
        if (preferredSide == PlacementSide.Below && fitsBelow) side = PlacementSide.Below;
        else if (preferredSide == PlacementSide.Above && fitsAbove) side = PlacementSide.Above;
        else if (fitsAbove) side = PlacementSide.Above;
        else if (fitsBelow) side = PlacementSide.Below;
        else
        {
            // Neither side has room: pick the side with more space and clamp inside the work area.
            var roomAbove = anchor.Union.Top - work.Top;
            var roomBelow = work.Bottom - anchor.Union.Bottom;
            side = roomAbove >= roomBelow ? PlacementSide.Above : PlacementSide.Below;
        }

        var top = side == PlacementSide.Above ? aboveTop : belowTop;
        var minTop = work.Top + margin;
        var maxTop = work.Bottom - margin - cardHeight;
        top = maxTop < minTop ? minTop : Math.Clamp(top, minTop, maxTop);

        var card = PxRect.FromSize(left, top, cardWidth, cardHeight);
        var window = new PxRect(card.Left - insets.Left, card.Top - insets.Top, card.Right + insets.Right, card.Bottom + insets.Bottom);
        return new PopupPlacement(window, card, side, monitor);
    }

    /// <summary>Maximum card size for a monitor: never wider than ~440 logical px, never taller than 45% of the work area.</summary>
    public static (int MaxWidthPx, int MaxHeightPx) MaxCardSize(MonitorArea monitor, double maxWidthLogical = 440, double maxHeightFraction = 0.45)
    {
        var margin = monitor.Px(EdgeMarginLogical);
        var maxW = Math.Min(monitor.Px(maxWidthLogical), Math.Max(120, monitor.WorkArea.Width - 2 * margin));
        var maxH = Math.Max(monitor.Px(64), (int)(monitor.WorkArea.Height * maxHeightFraction));
        return (maxW, maxH);
    }
}
