// =============================================================================
// VIX_PositionGuard.cs — TradePhantoms Volatility Safety Indicator
// =============================================================================
// Platform : Quantower C# SDK (v1.143.x)
// Drop path: C:\Quantower\Settings\Scripts\Indicators\IOF
//
// Position size caps by VIX regime (Christopher's rule, 2026-05-16):
//   VIX > 25   →  MAX 10 micros   (EXTREME  — 100pt 1m candles confirmed)
//   VIX 18-25  →  MAX 20 micros   (ELEVATED — tread carefully)
//   VIX < 18   →  MAX 20-50       (NORMAL   — full size range)
//
// Public API for IOF v2 integration:
//   guard.IsAllowed(n)    → false if n contracts exceeds current cap
//   guard.MaxContractsNow() → current hard cap as int
//   guard.VixLevel        → live VIX price
//   guard.Regime          → VixRegime enum value
//
// -----------------------------------------------------------------------------
// PATCH NOTES
// -----------------------------------------------------------------------------
// 2026-05-16: Initial build.
//   - Live VIX symbol subscription via Core.Instance.GetSymbol
//   - HUD overlay: current VIX, regime label, max contracts now, threshold table
//   - Color-coded regime bar (green/yellow/red)
//   - All thresholds user-adjustable inputs
//   - Fail-safe: Unknown regime → most restrictive cap (same as Extreme)
// =============================================================================

using System;
using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace TradePhantoms
{
    [Indicator("VIX_PositionGuard", "VIX Position Guard", false, Version = "1.0.0")]
    public class VIX_PositionGuard : Indicator
    {
        [InputParameter("VIX Symbol", sortIndex: 0)]
        public string VixSymbol = "VIX";

        [InputParameter("VIX High Threshold (extreme)", sortIndex: 1, minimum: 15, maximum: 50, increment: 1)]
        public int ThresholdHigh = 25;

        [InputParameter("VIX Mid Threshold (elevated)", sortIndex: 2, minimum: 10, maximum: 40, increment: 1)]
        public int ThresholdMid = 18;

        [InputParameter("Max Contracts — Low VIX", sortIndex: 3, minimum: 1, maximum: 200, increment: 5)]
        public int MaxContractsLow = 50;

        [InputParameter("Max Contracts — Mid VIX", sortIndex: 4, minimum: 1, maximum: 100, increment: 5)]
        public int MaxContractsMid = 20;

        [InputParameter("Max Contracts — High VIX", sortIndex: 5, minimum: 1, maximum: 50, increment: 1)]
        public int MaxContractsHigh = 10;

        [InputParameter("HUD Position", sortIndex: 6, variants: new object[]
        {
            "Top Left", 0, "Top Right", 1, "Bottom Left", 2, "Bottom Right", 3
        })]
        public int HudPosition = 1;

        private Symbol vixSymbol;
        private double currentVix = 0;
        private VixRegime currentRegime = VixRegime.Unknown;

        private static readonly Color ColorGreen  = Color.FromArgb(0, 210, 100);
        private static readonly Color ColorYellow = Color.FromArgb(255, 200, 0);
        private static readonly Color ColorRed    = Color.FromArgb(220, 50, 50);
        private static readonly Color ColorGray   = Color.FromArgb(140, 140, 140);
        private static readonly Color HudBg       = Color.FromArgb(200, 15, 15, 20);

        public VIX_PositionGuard() : base()
        {
            Name           = "VIX_PositionGuard";
            Description    = "Real-time VIX safety guard — enforces max micro contract limits by volatility regime.";
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            try
            {
                vixSymbol = Core.Instance.GetSymbol(new GetSymbolRequestParameters { SymbolId = VixSymbol });
                if (vixSymbol != null)
                    vixSymbol.NewLast += OnVixTick;
                else
                    Log($"VIX_PositionGuard: symbol '{VixSymbol}' not found", StrategyLoggingLevel.Error);
            }
            catch (Exception ex)
            {
                Log($"VIX_PositionGuard init error: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        protected override void OnUpdate(UpdateArgs args) { }

        private void OnVixTick(Symbol symbol, Last last)
        {
            currentVix    = last.Price;
            currentRegime = ClassifyRegime(currentVix);
        }

        protected override void OnClose()
        {
            if (vixSymbol != null) vixSymbol.NewLast -= OnVixTick;
        }

        private VixRegime ClassifyRegime(double vix)
        {
            if (vix <= 0)             return VixRegime.Unknown;
            if (vix > ThresholdHigh)  return VixRegime.Extreme;
            if (vix >= ThresholdMid)  return VixRegime.Elevated;
            return VixRegime.Normal;
        }

        public int MaxContractsNow() => currentRegime switch
        {
            VixRegime.Extreme  => MaxContractsHigh,
            VixRegime.Elevated => MaxContractsMid,
            VixRegime.Normal   => MaxContractsLow,
            _                  => MaxContractsHigh   // fail safe — most restrictive
        };

        public bool IsAllowed(int requestedContracts) => requestedContracts <= MaxContractsNow();
        public double   VixLevel => currentVix;
        public VixRegime Regime  => currentRegime;

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            var gr = args.Graphics;
            int width = args.Rectangle.Width, height = args.Rectangle.Height;

            const int hudW = 220, hudH = 90, pad = 12, margin = 16;

            int x = HudPosition switch { 1 => width - hudW - margin, 3 => width - hudW - margin, _ => margin };
            int y = HudPosition switch { 2 => height - hudH - margin, 3 => height - hudH - margin, _ => margin };

            using var bgBrush = new SolidBrush(HudBg);
            gr.FillRectangle(bgBrush, x, y, hudW, hudH);

            Color regimeColor = currentRegime switch
            {
                VixRegime.Extreme  => ColorRed,
                VixRegime.Elevated => ColorYellow,
                VixRegime.Normal   => ColorGreen,
                _                  => ColorGray
            };

            using var barBrush = new SolidBrush(regimeColor);
            gr.FillRectangle(barBrush, x, y, 5, hudH);

            using var fontBig   = new Font("Consolas", 11f, FontStyle.Bold);
            using var fontMed   = new Font("Consolas",  9f, FontStyle.Regular);
            using var fontSmall = new Font("Consolas",  8f, FontStyle.Regular);
            using var whiteBrush  = new SolidBrush(Color.White);
            using var regimeBrush = new SolidBrush(regimeColor);
            using var grayBrush   = new SolidBrush(ColorGray);

            int tx = x + pad + 4;
            string vixText = currentVix > 0 ? $"VIX  {currentVix:F2}" : "VIX  --.-";
            gr.DrawString(vixText, fontBig, whiteBrush, tx, y + 8);

            string regimeLabel = currentRegime switch
            {
                VixRegime.Extreme  => "EXTREME VOLATILITY",
                VixRegime.Elevated => "ELEVATED VOLATILITY",
                VixRegime.Normal   => "NORMAL VOLATILITY",
                _                  => "VIX LOADING..."
            };
            gr.DrawString(regimeLabel, fontSmall, regimeBrush, tx, y + 32);

            gr.DrawString($"MAX CONTRACTS:  {MaxContractsNow()}", fontMed, whiteBrush, tx, y + 52);
            gr.DrawString($"> {ThresholdHigh}=10  |  {ThresholdMid}-{ThresholdHigh}=20  |  <{ThresholdMid}=50", fontSmall, grayBrush, tx, y + 72);
        }
    }

    public enum VixRegime { Unknown, Normal, Elevated, Extreme }
}
