using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Projecte1_WPF_.Simulation;

namespace Projecte1_WPF_
{
    // One block of the comparison: what does not change with the timeline time (MainWindow.BuildCompareSector)
    public class CompareSector
    {
        public string Name;
        public string Acc;
        public List<(double From, double To, sbyte Status)> Strip = new List<(double, double, sbyte)>(); // fractions of the simulation
        public string Entries = "-", Average = "-", Peak = "-", Near = "-", Over = "-";
    }

    // Its values at the timeline time (MainWindow.UpdateCompareNow)
    public class CompareNow
    {
        public string Config;          // "CONF8A", or "closed"
        public string LoadText;        // "81 %", "no limit", "closed"
        public sbyte Status;           // AirspaceTimeline.CLOSED or (sbyte)CapacityStatus
        public string Demand;          // "10 of 12.3"
    }

    public partial class ComparePanel : UserControl
    {
        private const double STRIP_W = 290, STRIP_H = 12;
        private static readonly Brush Grey = Charts.Frozen(0x5A, 0x67, 0x73);
        private static readonly Brush Link = Charts.Frozen(0x1F, 0x5F, 0xB4);
        private static readonly Brush Rule = Charts.Frozen(0xE3, 0xEA, 0xF0);

        private readonly Dictionary<string, (Border Pill, TextBlock Load, TextBlock Now, Line Cursor)> _live =
            new Dictionary<string, (Border, TextBlock, TextBlock, Line)>(StringComparer.OrdinalIgnoreCase);

        public event Action<string> OpenRequested;    // a sector's name clicked: its full panel
        public event Action<string> RemoveRequested;  // its × clicked: untick it
        public event EventHandler CloseRequested;

        public ComparePanel()
        {
            InitializeComponent();
        }

        // One block per sector, stacked: name and ACC, load now, the day strip, then five figures in two columns
        public void SetSectors(IList<CompareSector> sectors)
        {
            _live.Clear();
            List.Children.Clear();
            TitleText.Text = $"Comparing {sectors.Count} sectors";
            for (int i = 0; i < sectors.Count; i++)
            {
                CompareSector s = sectors[i];
                string name = s.Name;
                var block = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

                // Name (opens its full panel) and ACC; × unticks it
                var head = new DockPanel();
                var remove = new Button { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Width = 24, Height = 24,
                                          Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                                          VerticalAlignment = VerticalAlignment.Top, ToolTip = $"Untick {name}" };
                System.Windows.Automation.AutomationProperties.SetName(remove, "Untick " + name);
                remove.Click += (o, e) => RemoveRequested?.Invoke(name);
                DockPanel.SetDock(remove, Dock.Right);
                head.Children.Add(remove);
                var open = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                                        Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left,
                                        ToolTip = $"Open the panel of {name} and zoom to it" };
                System.Windows.Automation.AutomationProperties.SetName(open, "Open " + name);
                var title = new TextBlock();
                title.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = Link });
                title.Inlines.Add(new System.Windows.Documents.Run("   " + s.Acc) { FontSize = 11, Foreground = Grey });
                open.Content = title;
                open.Click += (o, e) => OpenRequested?.Invoke(name);
                head.Children.Add(open);
                block.Children.Add(head);

                // Now: load pill and entries against capacity, configuration (SetNow fills them)
                var nowRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var loadText = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12 };
                var pill = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 8, 0),
                                        VerticalAlignment = VerticalAlignment.Center, Child = loadText };
                DockPanel.SetDock(pill, Dock.Left);
                nowRow.Children.Add(pill);
                var nowText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                nowRow.Children.Add(nowText);
                block.Children.Add(nowRow);

                // The day: its status at every sliding step, grey = closed, the timeline time in blue
                var strip = new Canvas { Width = STRIP_W, Height = STRIP_H, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true,
                                         Margin = new Thickness(0, 8, 0, 0), Background = Charts.Closed,
                                         ToolTip = "Its status at every sliding step of the simulation: green, yellow, red; grey = closed. The blue mark is the timeline time." };
                foreach (var (from, to, status) in s.Strip)
                    if (status != AirspaceTimeline.CLOSED)
                        Charts.Rect(strip, from * STRIP_W, 0, Math.Max(0.6, (to - from) * STRIP_W), STRIP_H, Charts.StatusBrush(status));
                var cursor = Charts.Line(strip, 0, -2, 0, STRIP_H + 2, Charts.Cursor, 2);
                block.Children.Add(strip);

                // Over the simulation: five figures, two columns
                var figures = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
                figures.Children.Add(Figure("Entries", s.Entries, "Entries into the sector while open, whole simulation"));
                figures.Children.Add(Figure("Average inside", s.Average, "Average time from entry to exit"));
                figures.Children.Add(Figure("Peak load", s.Peak, "Highest load in one window while open, and when"));
                figures.Children.Add(Figure("Near the limit", s.Near, "Time shown yellow: 80 to 100 % of capacity"));
                figures.Children.Add(Figure("Over capacity", s.Over, "Time shown red: demand above capacity"));
                block.Children.Add(figures);

                if (i < sectors.Count - 1)
                    block.Children.Add(new Border { BorderBrush = Rule, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 10, 0, 0) });
                List.Children.Add(block);
                _live[name] = (pill, loadText, nowText, cursor);
            }
        }

        // Values at the timeline time; cursor = its fraction of the simulation
        public void SetNow(IDictionary<string, CompareNow> now, double cursor)
        {
            foreach (var kv in _live)
            {
                var (pill, load, nowText, line) = kv.Value;
                line.X1 = line.X2 = Math.Max(0, Math.Min(1, cursor)) * STRIP_W;
                if (!now.TryGetValue(kv.Key, out CompareNow n))
                    continue;
                load.Text = n.LoadText;
                pill.Background = n.Status == AirspaceTimeline.CLOSED ? Charts.Closed : StatusTint(n.Status);
                nowText.Text = n.Status == AirspaceTimeline.CLOSED ? "closed now" : $"{n.Demand} entries now, in {n.Config}";
            }
        }

        // The pill's background: the map colours, lighter, so the black figure stays readable
        private static Brush StatusTint(sbyte status) =>
            status == (sbyte)CapacityStatus.Red ? Charts.Frozen(0xF2, 0xA0, 0xA0)
            : status == (sbyte)CapacityStatus.Yellow ? Charts.Frozen(0xF6, 0xE0, 0x8A) : Charts.Frozen(0xB9, 0xE4, 0xB9);

        private static StackPanel Figure(string label, string value, string tip)
        {
            var p = new StackPanel { Margin = new Thickness(0, 4, 8, 2), ToolTip = tip, Background = Brushes.Transparent };
            p.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Grey });
            p.Children.Add(new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            return p;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
