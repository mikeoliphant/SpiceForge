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
    private readonly double[] portConstant;
    private double[]? last;

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

    // Which coefficients can be nonzero, from evaluating at several parameter settings (see Mark).
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

        // The eliminated ports' open-circuit values in terms of the step inputs, Q_L·yLinear. These don't change.
        portGain = Jagged(l.Length, parameters);
        portSource = Jagged(l.Length, sources);
        portConstant = new double[l.Length];
        for (int j = 0; j < l.Length; j++)
            for (int i = 0; i < unknowns; i++)
            {
                double weight = Q[l[j], i];
                if (weight == 0)
                    continue;
                portConstant[j] += weight * constant[i];
                for (int p = 0; p < parameters; p++)
                    portGain[j][p] += weight * parameterGain[i, p];
                for (int x = 0; x < sources; x++)
                    portSource[j][x] += weight * sourceGain[i, x];
            }
    }

    /// <summary>
    /// Recomputes the coefficients if the parameters changed since the last call. <paramref name="linearJacobian"/>
    /// gives Jl[t, l]: the derivative of linear term TL[t] with respect to port L[l] at the current parameters.
    /// </summary>
    public bool Update(double[] parameters, Func<double[,]> linearJacobian)
    {
        if (last != null && parameters.AsSpan().SequenceEqual(last))
            return false;
        last = (double[])parameters.Clone();
        Updates++;

        double[,] jl = linearJacobian();
        int kl = L.Length, kn = N.Length, ml = TL.Length, mn = TN.Length;

        // A = I - Z_LL·Jl, inverted by Gauss-Jordan with partial pivoting.
        var a = new double[kl, 2 * kl];
        for (int i = 0; i < kl; i++)
        {
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
        var m = new double[ml, kl];
        for (int t = 0; t < ml; t++)
            for (int j = 0; j < kl; j++)
            {
                double value = 0;
                for (int i = 0; i < kl; i++)
                    value += jl[t, i] * a[i, kl + j];
                m[t, j] = value;
            }

        // Each row type below is its static coefficient plus (row·M)·(the eliminated ports' version of the column).
        var through = new double[kl];
        void Through(Func<int, double> rowAtLinearTerm)
        {
            for (int j = 0; j < kl; j++)
            {
                double value = 0;
                for (int t = 0; t < ml; t++)
                {
                    double weight = rowAtLinearTerm(TL[t]);
                    if (weight != 0)
                        value += weight * m[t, j];
                }
                through[j] = value;
            }
        }
        double Fold(double baseValue, Func<int, double> portValue)
        {
            double value = baseValue;
            for (int j = 0; j < kl; j++)
                if (through[j] != 0)
                    value += through[j] * portValue(j);
            return value;
        }

        for (int r = 0; r < kn; r++)
        {
            int port = N[r];
            Through(t => z[port, t]);
            for (int c = 0; c < mn; c++)
                Zr[r][c] = Fold(z[port, TN[c]], j => z[L[j], TN[c]]);
            for (int f = 0; f < dz; f++)
                Qzr[r][f] = Fold(qz[port, f], j => qz[L[j], f]);
        }
        for (int f = 0; f < dz; f++)
        {
            Through(t => cp[f, t]);
            for (int c = 0; c < mn; c++)
                CPr[f][c] = Fold(cp[f, TN[c]], j => z[L[j], TN[c]]);
            for (int h = 0; h < dz; h++)
                Czr[f][h] = Fold(0, j => qz[L[j], h]);
            ConstraintConstant[f] = Fold(cc[f], j => portConstant[j]);
            for (int p = 0; p < ConstraintGain[f].Length; p++)
                ConstraintGain[f][p] = Fold(ch[f, p], j => portGain[j][p]);
            for (int x = 0; x < ConstraintSource[f].Length; x++)
                ConstraintSource[f][x] = Fold(cs[f, x], j => portSource[j][x]);
        }
        for (int i = 0; i < Affected.Length; i++)
        {
            if (!Affected[i])
                continue;
            Through(t => g[i, t]);
            Constant[i] = Fold(constant[i], j => portConstant[j]);
            for (int p = 0; p < Gain[i].Length; p++)
                Gain[i][p] = Fold(gain[i, p], j => portGain[j][p]);
            for (int x = 0; x < Source[i].Length; x++)
                Source[i][x] = Fold(sourceGain[i, x], j => portSource[j][x]);
            for (int c = 0; c < mn; c++)
                TermGain[i][c] = Fold(g[i, TN[c]], j => z[L[j], TN[c]]);
            for (int f = 0; f < dz; f++)
                FreeGain[i][f] = Fold(nz[i, f], j => qz[L[j], f]);
        }
        return true;
    }

    /// <summary>Records which coefficients are nonzero at the current parameters.</summary>
    public void Mark()
    {
        Or(ZrUsed, Zr);
        Or(QzrUsed, Qzr);
        Or(CPrUsed, CPr);
        Or(CzrUsed, Czr);
        Or(GainUsed, Gain);
        Or(SourceUsed, Source);
        Or(TermGainUsed, TermGain);
        Or(FreeGainUsed, FreeGain);
        Or(ConstraintGainUsed, ConstraintGain);
        Or(ConstraintSourceUsed, ConstraintSource);
    }

    private static void Or(bool[][] used, double[][] values)
    {
        for (int i = 0; i < used.Length; i++)
            for (int j = 0; j < used[i].Length; j++)
                used[i][j] |= values[i][j] != 0;
    }

    private static bool[][] Flags(double[][] like) => like.Select(row => new bool[row.Length]).ToArray();

    private static double[][] Jagged(int rows, int columns) => Enumerable.Range(0, rows).Select(_ => new double[columns]).ToArray();
}
