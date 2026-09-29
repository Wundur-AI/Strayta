namespace Strayta.Core.Selection;

/// <summary>
/// Exact Euclidean distance transform with the nearest site (a "feature transform"): for every pixel of a grid, the
/// squared distance to the closest marked pixel and which one it is. Two separable passes of the lower-envelope
/// algorithm (Felzenszwalb and Huttenlocher, "Distance Transforms of Sampled Functions", 2012), columns then rows, so
/// the cost is linear in the pixel count whatever the distances.
/// </summary>
public static class DistanceTransform
{
    /// <summary>
    /// For each pixel, the index (y·w + x) of the nearest pixel where <paramref name="sites"/> is non-zero, or −1 when
    /// there is none; and its squared distance (float.PositiveInfinity when none).
    /// </summary>
    public static (int[] Nearest, float[] DistanceSquared) Compute(byte[] sites, int w, int h)
    {
        if (sites.Length != w * h) throw new ArgumentException("Expected one byte per pixel.", nameof(sites));
        // Pass 1, columns: the nearest site in the same column.
        var columnDist = new float[w * h];
        var columnSite = new int[w * h];
        Parallel.For(0, w, () => new Envelope(h), (x, _, env) =>
        {
            env.Reset();
            for (int y = 0; y < h; y++)
                if (sites[y * w + x] != 0) env.Add(y, 0f);
            for (int y = 0; y < h; y++)
            {
                int i = y * w + x;
                if (env.Count == 0)
                {
                    columnDist[i] = float.PositiveInfinity;
                    columnSite[i] = -1;
                    continue;
                }
                int q = env.Nearest(y, out float d);
                columnDist[i] = d;
                columnSite[i] = q * w + x;
            }
            return env;
        }, _ => { });

        // Pass 2, rows: the column results as parabola heights give the nearest site overall.
        var dist = new float[w * h];
        var nearest = new int[w * h];
        Parallel.For(0, h, () => new Envelope(w), (y, _, env) =>
        {
            env.Reset();
            int row = y * w;
            for (int x = 0; x < w; x++)
                if (columnSite[row + x] >= 0) env.Add(x, columnDist[row + x]);
            for (int x = 0; x < w; x++)
            {
                if (env.Count == 0)
                {
                    dist[row + x] = float.PositiveInfinity;
                    nearest[row + x] = -1;
                    continue;
                }
                int q = env.Nearest(x, out float d);
                dist[row + x] = d;
                nearest[row + x] = columnSite[row + q];
            }
            return env;
        }, _ => { });
        return (nearest, dist);
    }

    /// <summary>The lower envelope of parabolas (x − q)² + f(q), queried in increasing x.</summary>
    private sealed class Envelope(int n)
    {
        private readonly int[] _q = new int[n];
        private readonly float[] _f = new float[n];
        private readonly double[] _z = new double[n + 1];
        private int _k = -1, _cursor;

        public int Count => _k + 1;

        public void Reset()
        {
            _k = -1;
            _cursor = 0;
        }

        /// <summary>Adds the parabola at <paramref name="q"/> (increasing) with height <paramref name="f"/>.</summary>
        public void Add(int q, float f)
        {
            if (_k < 0)
            {
                _k = 0;
                _q[0] = q;
                _f[0] = f;
                _z[0] = double.NegativeInfinity;
                _z[1] = double.PositiveInfinity;
                return;
            }
            double s;
            while (true)
            {
                int p = _q[_k];
                s = ((f + (double)q * q) - (_f[_k] + (double)p * p)) / (2.0 * (q - p));
                if (s <= _z[_k] && _k > 0) _k--;
                else break;
            }
            if (s <= _z[_k])
            {
                // The new parabola is below the only remaining one everywhere.
                _q[_k] = q;
                _f[_k] = f;
                _z[_k + 1] = double.PositiveInfinity;
                return;
            }
            _k++;
            _q[_k] = q;
            _f[_k] = f;
            _z[_k] = s;
            _z[_k + 1] = double.PositiveInfinity;
        }

        /// <summary>The site nearest to <paramref name="x"/> (queries must increase) and its squared distance.</summary>
        public int Nearest(int x, out float distance)
        {
            while (_cursor < _k && _z[_cursor + 1] < x) _cursor++;
            int q = _q[_cursor];
            distance = (float)((double)(x - q) * (x - q) + _f[_cursor]);
            return q;
        }
    }
}
