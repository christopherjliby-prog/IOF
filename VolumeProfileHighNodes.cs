using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace IOF
{
    [IndicatorExtension]
    public class VolumeProfileHighNodes : Indicator
    {
        [InputParameter("Volume Threshold", 0, 1, 10000000, 100, 1)]
        public int VolumeThreshold = 1500;

        // Tick-size multiplier for price bucketing (1 = per tick, 2 = per 2 ticks, etc.)
        [InputParameter("Ticks Per Node", 1, 1, 100, 1, 0)]
        public int TicksPerNode = 1;

        private readonly Dictionary<double, double> volumeProfile = new();
        private double tickSize = 0.01;
        private bool needsRebuild = true;

        public VolumeProfileHighNodes() : base()
        {
            Name = "Volume Profile High Nodes";
            Description = "Draws a horizontal red line at every volume profile price node that meets or exceeds the volume threshold.";
            SeparateWindow = false;
            UpdateType = IndicatorUpdateType.OnBarClose;
        }

        protected override void OnInit()
        {
            tickSize = Symbol?.TickSize ?? 0.01;
            if (tickSize <= 0) tickSize = 0.01;
            needsRebuild = true;
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            needsRebuild = true;
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            if (needsRebuild)
            {
                BuildVolumeProfile();
                needsRebuild = false;
            }

            var mainWindow = args.Windows[0];
            var graphics = args.Graphics;
            var rect = mainWindow.ClientRectangle;

            using var pen = new Pen(Color.Red, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Solid };

            foreach (var kvp in volumeProfile)
            {
                if (kvp.Value < VolumeThreshold)
                    continue;

                int y = (int)mainWindow.CoordByValue(kvp.Key);

                // Skip lines outside the visible price range
                if (y < rect.Top || y > rect.Bottom)
                    continue;

                graphics.DrawLine(pen, rect.Left, y, rect.Right, y);
            }
        }

        private void BuildVolumeProfile()
        {
            volumeProfile.Clear();

            double bucketSize = tickSize * Math.Max(1, TicksPerNode);
            int count = Count;

            for (int i = 0; i < count; i++)
            {
                double high   = GetPrice(PriceType.High,   i);
                double low    = GetPrice(PriceType.Low,    i);
                double volume = GetPrice(PriceType.Volume, i);

                if (volume <= 0) continue;

                double range = high - low;

                if (range < bucketSize)
                {
                    // Entire bar fits in one bucket
                    double bucket = RoundToBucket(low, bucketSize);
                    AddVolume(bucket, volume);
                    continue;
                }

                // Distribute volume evenly across all price buckets the bar spans
                int levels = (int)Math.Round(range / bucketSize) + 1;
                double volPerLevel = volume / levels;

                for (int j = 0; j < levels; j++)
                {
                    double price  = low + j * bucketSize;
                    double bucket = RoundToBucket(price, bucketSize);
                    AddVolume(bucket, volPerLevel);
                }
            }
        }

        private void AddVolume(double bucket, double volume)
        {
            if (volumeProfile.TryGetValue(bucket, out double existing))
                volumeProfile[bucket] = existing + volume;
            else
                volumeProfile[bucket] = volume;
        }

        private static double RoundToBucket(double price, double bucketSize)
        {
            return Math.Round(price / bucketSize) * bucketSize;
        }
    }
}
