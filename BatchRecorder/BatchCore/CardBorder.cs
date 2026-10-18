using System;
using System.Windows.Controls;
using System.Windows.Media;

namespace BatchRecorder
{
    /// <summary>
    /// A card its host may paint (2026-10-17: the Reluduali Designer prints its cards by day like reluduali.com's, its
    /// PrintCard.cs). With no painter set it is a plain Border with the host's Card style, as before.
    /// </summary>
    public class CardBorder : Border
    {
        /// <summary>Set by the host before any card is made: a painter for each new card (null: plain Borders).</summary>
        public static Func<CardBorder, ICardPainter> NewPainter;

        readonly ICardPainter _painter;

        public CardBorder()
        {
            var f = NewPainter;
            if (f != null) try { _painter = f(this); } catch { _painter = null; }
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_painter == null || !_painter.Render(dc)) base.OnRender(dc);
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters p)
        {
            HitTestResult r;
            if (_painter != null && _painter.HitTest(p, out r)) return r;
            return base.HitTestCore(p);
        }
    }

    /// <summary>What a host's painter does for a card.</summary>
    public interface ICardPainter
    {
        /// <summary>Draws the card (under its content); false leaves it to the plain Border.</summary>
        bool Render(DrawingContext dc);

        /// <summary>True with the result decides the hit test; false leaves it to the plain Border.</summary>
        bool HitTest(PointHitTestParameters p, out HitTestResult result);
    }
}
