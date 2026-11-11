namespace SpiceForge;

/// <summary>
/// Removes the ports that only linear terms read (pot legs, for example) from the step, by folding them into the
/// coefficients whenever a runtime parameter changes.
/// <para>
/// Ports split into N (read by nonlinear terms) and L (read only by linear terms), and terms into nonlinear qN and
/// linear qL = Jl·wL, where Jl depends only on runtime parameters. The port equations are
/// <c>wN = vocN + Z_NN·qN + Z_NL·qL + Qz_N·z</c> and <c>wL = vocL + Z_LN·qN + Z_LL·qL + Qz_L·z</c>, with
/// constraints <c>0 = cs + CP_N·qN + CP_L·qL</c>. Eliminating wL exactly, with A = I - Z_LL·Jl and M = Jl·A⁻¹:
/// </para>
/// <code>
///   wN = Q_N·yEff + Zr·qN + Qzr·z                    Zr  = Z_NN + Z_NL·M·Z_LN,  Qzr = Qz_N + Z_NL·M·Qz_L
///   0  = csEff + CPr·qN + Czr·z                      CPr = CP_N + CP_L·M·Z_LN,  Czr = CP_L·M·Qz_L
///   y  = yEff + Geff·qN + Nzeff·z                    Geff = G_N + G_L·M·Z_LN,   Nzeff = Nz + G_L·M·Qz_L
///   yEff  = yLinear + G_L·M·Q_L·yLinear              (as gains on the step inputs)
///   csEff = cs + CP_L·M·Q_L·yLinear
/// </code>
/// These are the circuit's own coefficients at the current parameter values, so the step does the same work as
/// with the parameters fixed. A depends on the parameters only through multiplication, so it stays well
/// conditioned at both pot end stops (at P = 0 it is the identity).
/// </summary>
internal sealed class LinearPortReduction
{
    public int[] N { get; }
    public int[] L { get; }
    public int[] TN { get; }
    public int[] TL { get; }

    private readonly double[,] z, qz, cp, gain, sourceGain, g, nz, ch, cs;
    private readonly double[] constant, cc;
    private readonly int dz;
    private readonly double[][] portGain, portSource;
    // The sum of the magnitudes of the terms that make up each port gain, to tell cancellations from small values.
    private readonly double[][] portGainSize, portSourceSize;
    private readonly double[] portConstant;

    /// <summary>Unknowns a linear term reaches (G_L nonzero); only their coefficients change with the parameters.</summary>
    public bool[] Affected { get; }

    // Current coefficients. Jagged so compiled code can read them directly.
    public double[][] Zr { get; }
    public double[][] Qzr { get; }
    public double[][] CPr { get; }
    public double[][] Czr { get; }
    public double[][] Gain { get; }
    public double[] Constant { get; }
    public double[][] Source { get; }
    public double[][] TermGain { get; }
    public double[][] FreeGain { get; }
    public double[][] ConstraintGain { get; }
    public double[] ConstraintConstant { get; }
    public double[][] ConstraintSource { get; }

    // Which coefficients can be nonzero, from evaluating at several parameter settings while learning (see Freeze).
    // An entry counts only if it is more than rounding residue at some setting, since each one costs a live read and a
    // multiply-add in the compiled step:
    //  - Everywhere, it must be larger than CancellationTolerance times the sum of the magnitudes of the terms that make
    //    it up (including those of the eliminated ports' gains), so not an exact cancellation.
    //  - In the gains that rebuild the unknowns from the step inputs (Gain, Source), it must also be larger than
    //    RowTolerance times the largest entry in its row. Below that, its term is smaller than the rounding error the
    //    row's sum already carries. Many such entries are residue carried in from Z, G or A⁻¹ (a fifth of the Big
    //    Muff's live reads). The Newton coefficients and the term gains don't get this test: in high-gain circuits
    //    (the Rat), dropping even entries at 1e-17 of their row changes the solution.
    public bool[][] ZrUsed { get; }
    public bool[][] QzrUsed { get; }
    public bool[][] CPrUsed { get; }
    public bool[][] CzrUsed { get; }
    public bool[][] GainUsed { get; }
    public bool[][] SourceUsed { get; }
    public bool[][] TermGainUsed { get; }
    public bool[][] FreeGainUsed { get; }
    public bool[][] ConstraintGainUsed { get; }
    public bool[][] ConstraintSourceUsed { get; }

    private const double CancellationTolerance = 1e-12;
    private const double RowTolerance = 1e-16;

    /// <summary>
    /// While true, each update marks the coefficients that can be nonzero. Once frozen, entries that were never marked
    /// are kept at zero, so every engine sees exactly the coefficients the compiled step reads.
    /// </summary>
    public bool Learning { get; private set; } = true;

    // Each live array with its flags, whether it gets the row test, and each entry's size relative to its terms in the
    // update in progress (while learning).
    private readonly (bool[][] Used, double[][] Values, bool RowTest, double[][] Ratio)[] tracked;

    // Each tracked entry's value at the first learning setting, and whether a later one differed (see Varies).
    private readonly double[][][] firstValues;
    private readonly bool[][][] varies;
    private bool firstSeen;

    /// <summary>How many times the coefficients have been recomputed.</summary>
    public long Updates { get; private set; }

    public LinearPortReduction(int[] n, int[] l, int[] tn, int[] tl, double[,] Z, double[,] Qz, double[,] CP,
        double[,] Q, double[,] parameterGain, double[] constant, double[,] sourceGain, double[,] G, double[,] Nz,
        double[,] CH, double[] CC, double[,] CS)
    {
        N = n;
        L = l;
        TN = tn;
        TL = tl;
        z = Z;
        qz = Qz;
        cp = CP;
        gain = parameterGain;
        this.constant = constant;
        this.sourceGain = sourceGain;
        g = G;
        nz = Nz;
        ch = CH;
        cc = CC;
        cs = CS;
        dz = Qz.GetLength(1);
        int unknowns = G.GetLength(0), parameters = parameterGain.GetLength(1), sources = sourceGain.GetLength(1);

        Affected = Enumerable.Range(0, unknowns).Select(i => tl.Any(t => G[i, t] != 0)).ToArray();
        Zr = Jagged(n.Length, tn.Length);
        Qzr = Jagged(n.Length, dz);
        CPr = Jagged(dz, tn.Length);
        Czr = Jagged(dz, dz);
        Gain = Jagged(unknowns, parameters);
        Constant = new double[unknowns];
        Source = Jagged(unknowns, sources);
        TermGain = Jagged(unknowns, tn.Length);
        FreeGain = Jagged(unknowns, dz);
        ConstraintGain = Jagged(dz, parameters);
        ConstraintConstant = new double[dz];
        ConstraintSource = Jagged(dz, sources);
        ZrUsed = Flags(Zr);
        QzrUsed = Flags(Qzr);
        CPrUsed = Flags(CPr);
        CzrUsed = Flags(Czr);
        GainUsed = Flags(Gain);
        SourceUsed = Flags(Source);
        TermGainUsed = Flags(TermGain);
        FreeGainUsed = Flags(FreeGain);
        ConstraintGainUsed = Flags(ConstraintGain);
        ConstraintSourceUsed = Flags(ConstraintSource);
        tracked = new[]
        {
            (ZrUsed, Zr, false), (QzrUsed, Qzr, false), (CPrUsed, CPr, false), (CzrUsed, Czr, false),
            (GainUsed, Gain, true), (SourceUsed, Source, true), (TermGainUsed, TermGain, false),
            (FreeGainUsed, FreeGain, false), (ConstraintGainUsed, ConstraintGain, false),
            (ConstraintSourceUsed, ConstraintSource, false),
        }.Select(a => (a.Item1, a.Item2, a.Item3, Jagged(a.Item2.Length, a.Item2.Length == 0 ? 0 : a.Item2[0].Length))).ToArray();
        firstValues = tracked.Select(a => Jagged(a.Values.Length, a.Values.Length == 0 ? 0 : a.Values[0].Length)).ToArray();
        varies = tracked.Select(a => Flags(a.Values)).ToArray();

        // The eliminated ports' open-circuit values in terms of the step inputs, Q_L·yLinear. These don't change.
        portGain = Jagged(l.Length, parameters);
        portSource = Jagged(l.Length, sources);
        portGainSize = Jagged(l.Length, parameters);
        portSourceSize = Jagged(l.Length, sources);
        portConstant = new double[l.Length];
        for (int j = 0; j < l.Length; j++)
            for (int i = 0; i < unknowns; i++)
            {
                double weight = Q[l[j], i];
                if (weight == 0)
                    continue;
                portConstant[j] += weight * constant[i];
                for (int p = 0; p < parameters; p++)
                {
                    portGain[j][p] += weight * parameterGain[i, p];
                    portGainSize[j][p] += Math.Abs(weight * parameterGain[i, p]);
                }
                for (int x = 0; x < sources; x++)
                {
                    portSource[j][x] += weight * sourceGain[i, x];
                    portSourceSize[j][x] += Math.Abs(weight * sourceGain[i, x]);
                }
            }

        int kl = l.Length;
        work = new double[kl, 2 * kl];
        m = new double[tl.Length, kl];
        through = new double[kl];
        throughSize = new double[kl];
        last = new double[parameters];
        zColumns = tn.Select(c => Enumerable.Range(0, kl).Select(j => Z[l[j], c]).ToArray()).ToArray();
        qzColumns = Enumerable.Range(0, dz).Select(f => Enumerable.Range(0, kl).Select(j => Qz[l[j], f]).ToArray()).ToArray();
        gainColumns = Enumerable.Range(0, parameters).Select(p => Enumerable.Range(0, kl).Select(j => portGain[j][p]).ToArray()).ToArray();
        gainSizeColumns = Enumerable.Range(0, parameters).Select(p => Enumerable.Range(0, kl).Select(j => portGainSize[j][p]).ToArray()).ToArray();
        sourceColumns = Enumerable.Range(0, sources).Select(x => Enumerable.Range(0, kl).Select(j => portSource[j][x]).ToArray()).ToArray();
        sourceSizeColumns = Enumerable.Range(0, sources).Select(x => Enumerable.Range(0, kl).Select(j => portSourceSize[j][x]).ToArray()).ToArray();
    }

    /// <summary>
    /// Recomputes the coefficients if the parameters changed since the last call. <paramref name="linearJacobian"/>
    /// gives Jl[t, l]: the derivative of linear term TL[t] with respect to port L[l] at the current parameters.
    /// </summary>
    public bool Update(double[] parameters, Func<double[,]> linearJacobian)
    {
        // Allocation-free once constructed: hosts call this from the audio thread whenever a pot or switch moves.
        if (hasLast && parameters.AsSpan().SequenceEqual(last))
            return false;
        parameters.CopyTo(last, 0);
        hasLast = true;
        Updates++;

        double[,] jl = linearJacobian();
        int kl = L.Length, kn = N.Length, ml = TL.Length, mn = TN.Length;

        // A = I - Z_LL·Jl, inverted by Gauss-Jordan with partial pivoting.
        double[,] a = work;
        for (int i = 0; i < kl; i++)
        {
            for (int j = 0; j < 2 * kl; j++)
                a[i, j] = 0;
            for (int j = 0; j < kl; j++)
            {
                double value = i == j ? 1 : 0;
                for (int t = 0; t < ml; t++)
                    value -= z[L[i], TL[t]] * jl[t, j];
                a[i, j] = value;
            }
            a[i, kl + i] = 1;
        }
        for (int col = 0; col < kl; col++)
        {
            int pivot = col;
            for (int i = col + 1; i < kl; i++)
                if (Math.Abs(a[i, col]) > Math.Abs(a[pivot, col]))
                    pivot = i;
            if (a[pivot, col] == 0)
                throw new SingularSystemException("The linear ports' system is singular at these parameter values.");
            if (pivot != col)
                for (int j = 0; j < 2 * kl; j++)
                    (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
            double inverse = 1 / a[col, col];
            for (int j = 0; j < 2 * kl; j++)
                a[col, j] *= inverse;
            for (int i = 0; i < kl; i++)
            {
                if (i == col || a[i, col] == 0)
                    continue;
                double factor = a[i, col];
                for (int j = 0; j < 2 * kl; j++)
                    a[i, j] -= factor * a[col, j];
            }
        }

        // M = Jl·A⁻¹.
        for (int t = 0; t < ml; t++)
            for (int j = 0; j < kl; j++)
            {
                double value = 0;
                for (int i = 0; i < kl; i++)
                    value += jl[t, i] * a[i, kl + j];
                m[t, j] = value;
            }

        // Each row type below is its static coefficient plus (row·M)·(the eliminated ports' version of the column).
        for (int r = 0; r < kn; r++)
        {
            int port = N[r];
            Through(z, port);
            for (int c = 0; c < mn; c++)
                Zr[r][c] = Keep(0, r, c, z[port, TN[c]], zColumns[c], null);
            for (int f = 0; f < dz; f++)
                Qzr[r][f] = Keep(1, r, f, qz[port, f], qzColumns[f], null);
        }
        for (int f = 0; f < dz; f++)
        {
            Through(cp, f);
            for (int c = 0; c < mn; c++)
                CPr[f][c] = Keep(2, f, c, cp[f, TN[c]], zColumns[c], null);
            for (int h = 0; h < dz; h++)
                Czr[f][h] = Keep(3, f, h, 0, qzColumns[h], null);
            ConstraintConstant[f] = Fold(cc[f], portConstant);
            for (int p = 0; p < ConstraintGain[f].Length; p++)
                ConstraintGain[f][p] = Keep(8, f, p, ch[f, p], gainColumns[p], gainSizeColumns[p]);
            for (int x = 0; x < ConstraintSource[f].Length; x++)
                ConstraintSource[f][x] = Keep(9, f, x, cs[f, x], sourceColumns[x], sourceSizeColumns[x]);
        }
        for (int i = 0; i < Affected.Length; i++)
        {
            if (!Affected[i])
                continue;
            Through(g, i);
            Constant[i] = Fold(constant[i], portConstant);
            for (int p = 0; p < Gain[i].Length; p++)
                Gain[i][p] = Keep(4, i, p, gain[i, p], gainColumns[p], gainSizeColumns[p]);
            for (int x = 0; x < Source[i].Length; x++)
                Source[i][x] = Keep(5, i, x, sourceGain[i, x], sourceColumns[x], sourceSizeColumns[x]);
            for (int c = 0; c < mn; c++)
                TermGain[i][c] = Keep(6, i, c, g[i, TN[c]], zColumns[c], null);
            for (int f = 0; f < dz; f++)
                FreeGain[i][f] = Keep(7, i, f, nz[i, f], qzColumns[f], null);
        }
        if (Learning)
            MarkLearned();
        return true;
    }

    // Work space for Update, allocated once.
    private readonly double[,] work, m;
    private readonly double[] through, throughSize, last;
    private bool hasLast;
    // The eliminated ports' version of each column, as vectors over the eliminated ports: z[L, TN[c]], qz[L, f], and the
    // port gains by parameter and by source, with the sums of their terms' magnitudes.
    private readonly double[][] zColumns, qzColumns, gainColumns, gainSizeColumns, sourceColumns, sourceSizeColumns;

    // through = (the matrix's row at the linear terms)·M, and the sum of its terms' magnitudes.
    private void Through(double[,] matrix, int row)
    {
        int kl = L.Length, ml = TL.Length;
        for (int j = 0; j < kl; j++)
        {
            double value = 0, size = 0;
            for (int t = 0; t < ml; t++)
            {
                double weight = matrix[row, TL[t]];
                if (weight != 0)
                {
                    value += weight * m[t, j];
                    size += Math.Abs(weight * m[t, j]);
                }
            }
            through[j] = value;
            throughSize[j] = size;
        }
    }

    private double Fold(double baseValue, double[] portValues)
    {
        double value = baseValue;
        for (int j = 0; j < through.Length; j++)
            if (through[j] != 0)
                value += through[j] * portValues[j];
        return value;
    }

    // Folds an entry of tracked array a that can be read live. While learning, it records how far the entry is from
    // cancelling (see MarkLearned); once frozen, an entry that was never marked stays at zero, and one that doesn't
    // depend on the parameters stays at its learned value, so every engine sees exactly the coefficients the compiled
    // step reads or inlines. portSizes, when given, are the sums of the magnitudes of the terms behind portValues.
    private double Keep(int a, int row, int column, double baseValue, double[] portValues, double[]? portSizes)
    {
        double value = Fold(baseValue, portValues);
        if (!Learning)
        {
            if (!tracked[a].Used[row][column])
                return 0;
            return varies[a][row][column] ? value : firstValues[a][row][column];
        }
        double size = Math.Abs(baseValue);
        for (int j = 0; j < through.Length; j++)
            if (throughSize[j] != 0)
                size += throughSize[j] * (portSizes == null ? Math.Abs(portValues[j]) : portSizes[j]);
        tracked[a].Ratio[row][column] = size == 0 ? 0 : Math.Abs(value) / size;
        return value;
    }

    // Marks the entries that are more than residue at the current parameters (see the Used properties).
    private void MarkLearned()
    {
        for (int a = 0; a < tracked.Length; a++)
            for (int i = 0; i < tracked[a].Values.Length; i++)
                for (int c = 0; c < tracked[a].Values[i].Length; c++)
                {
                    double v = tracked[a].Values[i][c];
                    if (!firstSeen)
                        firstValues[a][i][c] = v;
                    else if (v != firstValues[a][i][c])
                        varies[a][i][c] = true;
                }
        firstSeen = true;

        foreach (var (used, values, rowTest, ratios) in tracked)
            for (int i = 0; i < values.Length; i++)
            {
                double floor = 0;
                if (rowTest)
                    foreach (double v in values[i])
                        floor = Math.Max(floor, RowTolerance * Math.Abs(v));
                for (int c = 0; c < values[i].Length; c++)
                    if (ratios[i][c] > CancellationTolerance && Math.Abs(values[i][c]) > floor)
                        used[i][c] = true;
            }
    }

    /// <summary>
    /// Ends learning, and recomputes the coefficients at these parameters with the entries never marked held at zero.
    /// </summary>
    public void Freeze(double[] parameters, Func<double[,]> linearJacobian)
    {
        Learning = false;
        hasLast = false;
        Update(parameters, linearJacobian);
    }

    /// <summary>
    /// Whether this live coefficient changed between the learning settings, so it depends on the parameters. One that
    /// doesn't can be compiled as a constant (its current value) rather than read from its array. Only meaningful once
    /// frozen. <paramref name="matrix"/> is one of the coefficient arrays above.
    /// </summary>
    public bool Varies(double[][] matrix, int row, int column)
    {
        int a = Array.FindIndex(tracked, t => ReferenceEquals(t.Values, matrix));
        if (a < 0)
            throw new ArgumentException("Not one of the reduction's coefficient arrays.", nameof(matrix));
        return varies[a][row][column];
    }

    /// <summary>Whether any coefficient in this row of <paramref name="matrix"/> depends on the parameters.</summary>
    public bool RowVaries(double[][] matrix, int row)
    {
        int a = Array.FindIndex(tracked, t => ReferenceEquals(t.Values, matrix));
        return a >= 0 && varies[a][row].Any(v => v);
    }

    private static bool[][] Flags(double[][] like) => like.Select(row => new bool[row.Length]).ToArray();

    private static double[][] Jagged(int rows, int columns) => Enumerable.Range(0, rows).Select(_ => new double[columns]).ToArray();
}
