using System;
using System.Collections.Generic;
using System.Linq;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.NTS;

namespace Projecte1_WPF_.Simulation
{
    // TRAFFIC DENSITY of the whole simulation: aircraft-minutes per CELL_DEG cell, smoothed. Each flight is sampled every
    // SAMPLE while it is drawn on the map (inside an open sector), so it shows where the traffic of the ACCs flows: routes,
    // junctions, climb and descent areas. Only the positions between the flight levels shown count (the same display
    // filter as the map). The samples are close enough (5 s, about 1 km at cruise speed) to make continuous tracks on the
    // 0.02° grid (about 2 km), and a Gaussian blur (BLUR_CELLS) turns them into a smooth surface. No UI; PlotDensity
    // renders it as one image
    public class TrafficDensity
    {
        public const double CELL_DEG = 0.02;
        private const double BLUR_CELLS = 1.3;
        private const double MARGIN_DEG = 0.2;   // room for the blur around the area
        private static readonly TimeSpan SAMPLE = TimeSpan.FromSeconds(5);

        public double MinLat { get; }
        public double MinLon { get; }
        public int Rows { get; }
        public int Cols { get; }
        public double MaxLat => MinLat + Rows * CELL_DEG;
        public double MaxLon => MinLon + Cols * CELL_DEG;
        public double Total { get; }

        // Smoothed aircraft-minutes per cell (row 0 = south)
        public double[,] Smoothed { get; }

        // Values of the colour ramp (smoothed aircraft-minutes per cell) at the quantiles QUANTILES of the cells with
        // traffic: the colours follow the distribution, so every colour is used whatever the total
        public static readonly double[] QUANTILES = { 0.05, 0.40, 0.65, 0.82, 0.92, 0.97, 1.0 };
        public double[] Knots { get; }

        public TrafficDensity(TimeSimulation simulation, LatLonBox area, int flFrom, int flTo)
        {
            MinLat = Math.Floor((area.MinLat - MARGIN_DEG) / CELL_DEG) * CELL_DEG;
            MinLon = Math.Floor((area.MinLon - MARGIN_DEG) / CELL_DEG) * CELL_DEG;
            Rows = (int)Math.Ceiling((area.MaxLat + MARGIN_DEG - MinLat) / CELL_DEG);
            Cols = (int)Math.Ceiling((area.MaxLon + MARGIN_DEG - MinLon) / CELL_DEG);
            var raw = new double[Rows, Cols];
            double perSample = SAMPLE.TotalMinutes, total = 0;

            foreach (var (flight, inside) in simulation.Stays())
                foreach (Interval stay in inside)
                    for (DateTime t = stay.Start; t < stay.End; t += SAMPLE)
                    {
                        if (!flight.TryGetPosition(t, out double lat, out double lon, out double fl))
                            continue;
                        if (fl < flFrom || (fl >= flTo && flTo < SectorPlanBuilder.NO_UPPER_LIMIT))
                            continue;
                        int r = (int)((lat - MinLat) / CELL_DEG), c = (int)((lon - MinLon) / CELL_DEG);
                        if (r < 0 || r >= Rows || c < 0 || c >= Cols)
                            continue;
                        raw[r, c] += perSample;
                        total += perSample;
                    }
            Total = total;
            Smoothed = Blur(raw, BLUR_CELLS);

            // Ramp knots: quantiles of the cells that carry something (above 1 s of traffic after the blur)
            var values = new List<double>();
            foreach (double v in Smoothed)
                if (v > 1.0 / 60)
                    values.Add(v);
            values.Sort();
            Knots = QUANTILES.Select(q => values.Count == 0 ? 0 : values[Math.Min(values.Count - 1, (int)(q * values.Count))]).ToArray();
        }

        // Separable Gaussian blur (sigma in cells), the total is kept
        private static double[,] Blur(double[,] grid, double sigma)
        {
            int rows = grid.GetLength(0), cols = grid.GetLength(1), radius = (int)Math.Ceiling(3 * sigma);
            var kernel = new double[2 * radius + 1];
            double sum = 0;
            for (int i = -radius; i <= radius; i++)
                sum += kernel[i + radius] = Math.Exp(-i * i / (2 * sigma * sigma));
            for (int i = 0; i < kernel.Length; i++)
                kernel[i] /= sum;

            var across = new double[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    double v = grid[r, c];
                    if (v == 0) continue;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int cc = c + k;
                        if (cc >= 0 && cc < cols) across[r, cc] += v * kernel[k + radius];
                    }
                }
            var result = new double[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    double v = across[r, c];
                    if (v == 0) continue;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int rr = r + k;
                        if (rr >= 0 && rr < rows) result[rr, c] += v * kernel[k + radius];
                    }
                }
            return result;
        }

        // Smoothed value at a position (bilinear between cell centres), 0 outside the grid
        public double ValueAt(double lat, double lon)
        {
            double y = (lat - MinLat) / CELL_DEG - 0.5, x = (lon - MinLon) / CELL_DEG - 0.5;
            int r0 = (int)Math.Floor(y), c0 = (int)Math.Floor(x);
            double fy = y - r0, fx = x - c0;
            double V(int r, int c) => r < 0 || r >= Rows || c < 0 || c >= Cols ? 0 : Smoothed[r, c];
            return (V(r0, c0) * (1 - fx) + V(r0, c0 + 1) * fx) * (1 - fy) + (V(r0 + 1, c0) * (1 - fx) + V(r0 + 1, c0 + 1) * fx) * fy;
        }

        // Position of a value on the colour ramp: below 0 = nothing shown, 0..1 between the first and the last knot
        public double RampPosition(double value)
        {
            if (value <= Knots[0]) return -1;
            for (int i = 1; i < Knots.Length; i++)
                if (value <= Knots[i])
                {
                    double span = Knots[i] - Knots[i - 1];
                    double f = span <= 0 ? 1 : (value - Knots[i - 1]) / span;
                    return (i - 1 + f) / (Knots.Length - 1);
                }
            return 1;
        }
    }
}
