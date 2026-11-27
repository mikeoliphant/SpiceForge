using System;
using System.Text;
using System.Threading;
using AudioPlugSharp;

namespace SpiceForgePlugin
{
    /// <summary>
    /// A host parameter for one of the circuit's switches. A parameter's steps are fixed once the host has it, while
    /// each schematic's switches have positions of their own, so the value is a plain 0 to 1: position p of n is
    /// p / (n - 1), and a value is in the position it falls in when 0 to 1 is cut into n equal parts (as VST3 divides a
    /// stepped parameter), so an on/off switch is on from 0.5. It shows "Off" and "On", or the position number.
    /// </summary>
    public class SwitchPositionParameter : AudioPluginParameter
    {
        int positions = 2;

        public SwitchPositionParameter()
        {
            MinValue = 0;
            MaxValue = 1;
            DefaultValue = 0;
        }

        /// <summary>The positions of the switch it controls, for showing its value (2 until a circuit has one).</summary>
        public int Positions
        {
            get { return Volatile.Read(ref positions); }
            set { Volatile.Write(ref positions, Math.Max(2, value)); }
        }

        /// <summary>The value of position <paramref name="position"/> of <paramref name="positions"/>.</summary>
        public static double ToValue(int position, int positions)
        {
            return (positions > 1) ? (double)position / (positions - 1) : 0;
        }

        /// <summary>The position of <paramref name="positions"/> a value is in.</summary>
        public static int ToPosition(double value, int positions)
        {
            return (int)Math.Min(positions - 1, Math.Floor(Math.Clamp(value, 0, 1) * positions));
        }

        public override string FormatValue(double value)
        {
            StringBuilder text = new StringBuilder();

            AppendValue(value, text);

            return text.ToString();
        }

        protected override void AppendValue(double value, StringBuilder text)
        {
            int count = Positions;
            int position = ToPosition(value, count);

            if (count == 2)
                text.Append((position == 1) ? "On" : "Off");
            else
                text.Append(position);
        }
    }
}
