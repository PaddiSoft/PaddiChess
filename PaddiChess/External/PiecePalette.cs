namespace PaddiXiangqi.External;

/// <summary>Appearance colours learned from glyph-labelled pieces, without predefined side colours.</summary>
internal static class PiecePalette
{
    internal sealed record Colour(double R, double G, double B, double Weight = 1)
    {
        public double Distance(Colour other) => Math.Sqrt((Math.Pow(R-other.R,2) + Math.Pow(G-other.G,2) + Math.Pow(B-other.B,2))/3);
    }

    internal static Colour[] Cluster(IReadOnlyList<Colour> samples, int count = 3)
    {
        if (samples.Count == 0) return [];
        var centres = new List<Colour> { samples[samples.Count / 2] };
        while (centres.Count < count)
        {
            var next = samples.MaxBy(p => centres.Min(c => c.Distance(p)))!;
            if (centres.Min(c => c.Distance(next)) < .03) break;
            centres.Add(next);
        }
        for (int iteration = 0; iteration < 8; iteration++)
        {
            var sums = new double[centres.Count,4];
            for (int i = 0; i < samples.Count; i++)
            {
                var p = samples[i]; int closest = 0; double distance = double.MaxValue;
                for (int c = 0; c < centres.Count; c++)
                {
                    var d = p.Distance(centres[c]);
                    if (d < distance) { distance = d; closest = c; }
                }
                sums[closest,0] += p.R; sums[closest,1] += p.G; sums[closest,2] += p.B; sums[closest,3]++;
            }
            for (int c = 0; c < centres.Count; c++)
                if (sums[c,3] > 0) centres[c] = new(sums[c,0]/sums[c,3],sums[c,1]/sums[c,3],sums[c,2]/sums[c,3],sums[c,3]/samples.Count);
        }
        return centres.Where(c => c.Weight >= .08).ToArray();
    }

    internal static Colour[] FromPatch(float[] patch)
    {
        var pixels = new List<Colour>(160);
        for (int y = 4; y < 20; y++) for (int x = 4; x < 20; x++)
        {
            // Ignore the selection ring and board; keep both lettering and disc colour.
            if ((x-11.5)*(x-11.5)+(y-11.5)*(y-11.5)>49) continue;
            int p = (y*24+x)*3;
            pixels.Add(new(patch[p],patch[p+1],patch[p+2]));
        }
        // Modal colours are insensitive to how much antialiased lettering each
        // character contains. A third grey edge shade is not another side palette.
        var buckets=pixels.GroupBy(p => ((int)(p.R*15+.5),(int)(p.G*15+.5),(int)(p.B*15+.5)))
            .OrderByDescending(g => g.Count()).ToArray();
        var modes=new List<Colour>();
        int minimum=Math.Max(5,buckets[0].Count()/5);
        foreach(var group in buckets.Where(g=>g.Count()>=minimum))
        {
            var colour=new Colour(group.Average(p=>p.R),group.Average(p=>p.G),group.Average(p=>p.B),group.Count()/(double)pixels.Count);
            if (modes.All(c=>c.Distance(colour)>.08)) modes.Add(colour);
            if (modes.Count==2) break;
        }
        return modes.ToArray();
    }

    // Thin lettering and smoothly shaded material can spread a side colour over
    // many bins, leaving the two modal colours almost neutral. Only when modal
    // comparison is ambiguous, count actual pixels close to semantic anchors.
    // Shared face colours cast no vote; neither hue nor a previous board is used.
    internal static bool TryResolveSide(float[] patch, Colour[][] redAnchors, Colour[][] blackAnchors, out bool red)
    {
        red = false;
        var reds = redAnchors.SelectMany(p => p).ToArray();
        var blacks = blackAnchors.SelectMany(p => p).ToArray();
        if (reds.Length == 0 || blacks.Length == 0) return false;
        int redVotes = 0, blackVotes = 0;
        for (int y = 4; y < 20; y++) for (int x = 4; x < 20; x++)
        {
            if ((x - 11.5) * (x - 11.5) + (y - 11.5) * (y - 11.5) > 49) continue;
            int at = (y * 24 + x) * 3;
            var p = new Colour(patch[at], patch[at + 1], patch[at + 2]);
            double r = reds.Min(p.Distance), b = blacks.Min(p.Distance);
            if (r < .12 && b - r > .08) redVotes++;
            else if (b < .12 && r - b > .08) blackVotes++;
        }
        red = redVotes > blackVotes;
        return Math.Max(redVotes, blackVotes) >= 8 && Math.Max(redVotes, blackVotes) > Math.Min(redVotes, blackVotes) * 3;
    }

    internal static double Distance(Colour[] a, Colour[] b)
    {
        if (a.Length == 0 || b.Length == 0) return 1;
        // Compare colour sets, not ink coverage: 馬 and 車 can have very different
        // stroke density while using exactly the same two colours.
        double Directed(Colour[] from, Colour[] to) => from.Sum(c => Math.Sqrt(c.Weight) * to.Min(c.Distance)) / from.Sum(c => Math.Sqrt(c.Weight));
        return (Directed(a,b) + Directed(b,a))/2;
    }
}
