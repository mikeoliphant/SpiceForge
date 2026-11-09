using System.Collections.Generic;

namespace SpiceForge.ReferenceCapture
{
    /// <summary>
    /// Records, for each Newton system and oversampled timestep, how many iterations ran.
    /// Called from generated code once per iteration, after the convergence test.
    /// </summary>
    public sealed class NewtonRecorder
    {
        /// <summary>Set on a step's entry when the iteration cap was reached without converging.</summary>
        public const byte CapFlag = 0x80;

        private int cap;
        private int[] current = new int[0];

        /// <summary>One byte per oversampled step, per Newton system: iteration count, ORed with CapFlag.</summary>
        public List<byte>[] Steps { get; private set; } = new List<byte>[0];

        public void Begin(int systems, int iterationCap)
        {
            cap = iterationCap;
            current = new int[systems];
            Steps = new List<byte>[systems];
            for (int i = 0; i < systems; i++)
                Steps[i] = new List<byte>();
        }

        public void Iteration(int system, bool done)
        {
            int count = ++current[system];
            if (!done && count < cap)
                return;

            Steps[system].Add((byte)(done ? count : count | CapFlag));
            current[system] = 0;
        }
    }
}
