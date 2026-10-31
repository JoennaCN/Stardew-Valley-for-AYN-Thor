using System;
using System.Globalization;
using System.IO;

namespace Rgdsplus.VerticalWorld
{
    // Pure geometry/configuration model. Keep this free of game dependencies so
    // every supported handheld profile can be regression-tested off-device.
    internal readonly struct DisplayRect
    {
        internal DisplayRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
        }

        internal int X { get; }
        internal int Y { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal int Right => X + Width;
        internal int Bottom => Y + Height;

        internal bool Contains(float x, float y) => x >= X && y >= Y && x < Right && y < Bottom;

        public override string ToString() => X + "," + Y + "," + Width + "," + Height;
    }

    internal sealed class DisplayProfile
    {
        private const int DefaultWidth = 1024;
        private const int DefaultHeight = 768;

        internal static DisplayProfile Current { get; private set; } = FromEnvironment(Environment.GetEnvironmentVariable);

        internal static void Configure(string requestedProfile)
        {
            string profile = requestedProfile?.Trim() ?? "auto";
            bool thor = string.Equals(profile, "ayn-thor-armada", StringComparison.OrdinalIgnoreCase)
                || string.Equals(profile, "auto", StringComparison.OrdinalIgnoreCase) && IsArmadaThor();
            if (!thor)
            {
                Current = FromEnvironment(Environment.GetEnvironmentVariable);
                return;
            }
            Current = FromEnvironment(name => Environment.GetEnvironmentVariable(name) ?? ThorDefault(name));
        }

        private DisplayProfile(DisplayRect upperPanel, DisplayRect lowerPanel,
            int upperReferenceWidth, int upperReferenceHeight,
            int lowerReferenceWidth, int lowerReferenceHeight, bool separateOutputs)
        {
            UpperPanel = upperPanel;
            LowerPanel = lowerPanel;
            UpperReferenceWidth = Math.Max(320, upperReferenceWidth);
            UpperReferenceHeight = Math.Max(240, upperReferenceHeight);
            LowerReferenceWidth = Math.Max(320, lowerReferenceWidth);
            LowerReferenceHeight = Math.Max(240, lowerReferenceHeight);
            SeparateOutputs = separateOutputs;
            UpperContent = Fit(upperPanel, UpperReferenceWidth, UpperReferenceHeight);
            LowerContent = Fit(lowerPanel, LowerReferenceWidth, LowerReferenceHeight);
            BackBufferWidth = Math.Max(UpperPanel.Right, LowerPanel.Right);
            BackBufferHeight = Math.Max(UpperPanel.Bottom, LowerPanel.Bottom);
        }

        internal DisplayRect UpperPanel { get; }
        internal DisplayRect LowerPanel { get; }
        internal DisplayRect UpperContent { get; }
        internal DisplayRect LowerContent { get; }
        internal int UpperReferenceWidth { get; }
        internal int UpperReferenceHeight { get; }
        internal int LowerReferenceWidth { get; }
        internal int LowerReferenceHeight { get; }
        internal bool SeparateOutputs { get; }
        internal int BackBufferWidth { get; }
        internal int BackBufferHeight { get; }

        internal bool TryMapLower(float x, float y, int uiWidth, int uiHeight, out int mappedX, out int mappedY)
        {
            mappedX = mappedY = 0;
            if (uiWidth <= 0 || uiHeight <= 0) return false;
            float sharedX = x;
            float sharedY = y;
            if (!LowerContent.Contains(sharedX, sharedY))
            {
                sharedX = x + LowerPanel.X;
                sharedY = y + LowerPanel.Y;
            }
            if (!LowerContent.Contains(sharedX, sharedY)) return false;
            mappedX = Math.Min(uiWidth - 1, (int)((sharedX - LowerContent.X) * uiWidth / LowerContent.Width));
            mappedY = Math.Min(uiHeight - 1, (int)((sharedY - LowerContent.Y) * uiHeight / LowerContent.Height));
            return true;
        }

        // Explicit bounds are the portable contract for compositors which expose
        // differently-sized or offset outputs. Size/layout variables are a simpler
        // fallback for ordinary horizontal or vertical arrangements.
        internal static DisplayProfile FromEnvironment(Func<string, string> read)
        {
            int referenceWidth = Positive(read("SDV_DUAL_REFERENCE_WIDTH"), DefaultWidth);
            int referenceHeight = Positive(read("SDV_DUAL_REFERENCE_HEIGHT"), DefaultHeight);
            int upperReferenceWidth = Positive(read("SDV_DUAL_UPPER_LOGICAL_WIDTH"), referenceWidth);
            int upperReferenceHeight = Positive(read("SDV_DUAL_UPPER_LOGICAL_HEIGHT"), referenceHeight);
            int lowerReferenceWidth = Positive(read("SDV_DUAL_LOWER_LOGICAL_WIDTH"), referenceWidth);
            int lowerReferenceHeight = Positive(read("SDV_DUAL_LOWER_LOGICAL_HEIGHT"), referenceHeight);
            bool upperOnRight = string.Equals(read("SDV_RGDS_PHYSICAL_UPPER"), "right", StringComparison.OrdinalIgnoreCase);
            bool separateOutputs = string.Equals(read("SDV_DUAL_OUTPUT_MODE"), "lease", StringComparison.OrdinalIgnoreCase);

            int upperWidth = Positive(read("SDV_DUAL_UPPER_WIDTH"), DefaultWidth);
            int upperHeight = Positive(read("SDV_DUAL_UPPER_HEIGHT"), DefaultHeight);
            int lowerWidth = Positive(read("SDV_DUAL_LOWER_WIDTH"), DefaultWidth);
            int lowerHeight = Positive(read("SDV_DUAL_LOWER_HEIGHT"), DefaultHeight);
            bool vertical = string.Equals(read("SDV_DUAL_SCREEN_LAYOUT"), "vertical", StringComparison.OrdinalIgnoreCase);

            DisplayRect upper;
            DisplayRect lower;
            if (separateOutputs)
            {
                upper = new DisplayRect(0, 0, upperWidth, upperHeight);
                lower = new DisplayRect(0, 0, lowerWidth, lowerHeight);
            }
            else if (vertical)
            {
                upper = new DisplayRect(0, 0, upperWidth, upperHeight);
                lower = new DisplayRect(0, upperHeight, lowerWidth, lowerHeight);
            }
            else if (upperOnRight)
            {
                lower = new DisplayRect(0, 0, lowerWidth, lowerHeight);
                upper = new DisplayRect(lowerWidth, 0, upperWidth, upperHeight);
            }
            else
            {
                upper = new DisplayRect(0, 0, upperWidth, upperHeight);
                lower = new DisplayRect(upperWidth, 0, lowerWidth, lowerHeight);
            }

            if (TryParseBounds(read("SDV_DUAL_UPPER_BOUNDS"), out var explicitUpper)) upper = explicitUpper;
            if (TryParseBounds(read("SDV_DUAL_LOWER_BOUNDS"), out var explicitLower)) lower = explicitLower;
            return new DisplayProfile(upper, lower, upperReferenceWidth, upperReferenceHeight,
                lowerReferenceWidth, lowerReferenceHeight, separateOutputs);
        }

        internal static DisplayRect Fit(DisplayRect panel, int logicalWidth, int logicalHeight)
        {
            if (logicalWidth <= 0 || logicalHeight <= 0) return panel;
            double scale = Math.Min(panel.Width / (double)logicalWidth, panel.Height / (double)logicalHeight);
            int width = Math.Max(1, Math.Min(panel.Width, (int)Math.Round(logicalWidth * scale)));
            int height = Math.Max(1, Math.Min(panel.Height, (int)Math.Round(logicalHeight * scale)));
            return new DisplayRect(panel.X + (panel.Width - width) / 2, panel.Y + (panel.Height - height) / 2, width, height);
        }

        private static int Positive(string value, int fallback) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed : fallback;

        private static string ThorDefault(string name) => name switch
        {
            "SDV_DUAL_OUTPUT_MODE" => "lease",
            "SDV_DUAL_UPPER_WIDTH" => "1920",
            "SDV_DUAL_UPPER_HEIGHT" => "1080",
            "SDV_DUAL_LOWER_WIDTH" => "1240",
            "SDV_DUAL_LOWER_HEIGHT" => "1080",
            "SDV_DUAL_UPPER_LOGICAL_WIDTH" => "1920",
            "SDV_DUAL_UPPER_LOGICAL_HEIGHT" => "1080",
            "SDV_DUAL_LOWER_LOGICAL_WIDTH" => "1240",
            "SDV_DUAL_LOWER_LOGICAL_HEIGHT" => "1080",
            _ => null
        };

        private static bool IsArmadaThor()
        {
            if (!File.Exists("/usr/libexec/armada/device-env")
                && !File.Exists("/proc/self/root/usr/libexec/armada/device-env")) return false;
            foreach (string path in new[] { "/proc/device-tree/model", "/sys/firmware/devicetree/base/model",
                "/sys/devices/virtual/dmi/id/product_name" })
            {
                try
                {
                    if (File.Exists(path) && File.ReadAllText(path).IndexOf("thor", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                catch { }
            }
            return false;
        }

        private static bool TryParseBounds(string value, out DisplayRect bounds)
        {
            bounds = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string[] parts = value.Split(',');
            if (parts.Length != 4) return false;
            var values = new int[4];
            for (int i = 0; i < values.Length; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                    return false;
            if (values[2] <= 0 || values[3] <= 0) return false;
            bounds = new DisplayRect(values[0], values[1], values[2], values[3]);
            return true;
        }
    }
}
