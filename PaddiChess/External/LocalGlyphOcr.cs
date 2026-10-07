using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PaddiXiangqi.External;

/// <summary>Offline Chinese OCR, independent of client/theme. Loaded once, only used for board recovery/setup.</summary>
public static class LocalGlyphOcr
{
    public sealed record Glyph(string Text, double Confidence);
    private sealed record Model(InferenceModel Session, string[] Characters);
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly Lazy<Model> Instance = new(() =>
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "Ocr", "ch_PP-OCRv5_rec_mobile.onnx");
        var session = InferenceModel.Open("中文 OCR", file, [8, 3, 48, 160]);
        if (!session.Metadata.TryGetValue("character", out var dictionary))
        { session.Dispose(); throw new IOException("识字模型缺少中文字典，请重新安装完整客户端。"); }
        return new(session, ["", ..dictionary.Split('\n'), " "]);
    });

    public static Task PrepareAsync() => Instance.Value.Session.PrepareAsync();

    public sealed record ReadResult(Glyph[] Glyphs, int InferenceCells, int ReusedCells, int Batches, double ElapsedMs)
    {
        public double ModelLoadMs { get; init; }
        public double NativeInferenceMs { get; init; }
    }
    private sealed record Prepared(float[] Input, List<int> Indices);
    private sealed record Cached(float[] Input, Glyph Glyph);

    public static Task<Glyph[]> ReadAsync(byte[] png, BoardCalibration geometry, CancellationToken ct)
        => ReadPngAsync(png, geometry, ct);

    private static async Task<Glyph[]> ReadPngAsync(byte[] png, BoardCalibration geometry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pixels = await Task.Run(() => CapturedPixels.DecodePng(png), ct).ConfigureAwait(false);
        return (await ReadDetailedAsync(pixels, geometry, ct).ConfigureAwait(false)).Glyphs;
    }

    public static async Task<Glyph[]> ReadAsync(CapturedPixels pixels, BoardCalibration geometry, CancellationToken ct)
        => (await ReadDetailedAsync(pixels, geometry, ct).ConfigureAwait(false)).Glyphs;

    /// <summary>Statistics describe actual inference work; reuse requires identical normalized input.</summary>
    public static async Task<ReadResult> ReadDetailedAsync(CapturedPixels pixels, BoardCalibration geometry, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => Read(pixels, geometry, ct), ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    /// <summary>Supplement only unresolved display squares; never rescan a confirmed board.</summary>
    public static async Task<ReadResult> ReadCellsAsync(CapturedPixels pixels, BoardCalibration geometry,
        IReadOnlyCollection<int> displayIndices, CancellationToken ct)
    {
        var requested = displayIndices.ToHashSet();
        if (requested.Any(i => i is < 0 or >= 90)) throw new ArgumentOutOfRangeException(nameof(displayIndices));
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => Read(pixels, geometry, ct, requested), ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private static ReadResult Read(CapturedPixels pixels, BoardCalibration geometry, CancellationToken ct, HashSet<int>? requested = null)
    {
        var timer = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();
        using var image = pixels.ToBitmap();
        geometry.Validate(image.Width, image.Height);
        var modelStart = Stopwatch.GetTimestamp();
        var model = Instance.Value;
        var modelLoadMs = Stopwatch.GetElapsedTime(modelStart).TotalMilliseconds;
        double nativeInferenceMs = 0;
        var results = Enumerable.Repeat(new Glyph("", 0), 90).ToArray();
        var plainGrid = Enumerable.Range(0,90).Select(i => IsPlainGrid(pixels,geometry,i)).ToArray();
        // Repeated soldiers and plain intersections often have exactly the same input.
        // This is per-request, bounded and verified byte-for-byte after hashing: a
        // different colour, stroke, highlight or animation still receives its own OCR.
        var cache = new Dictionary<string, Cached>();
        int inferenceCells = 0, reusedCells = 0, batches = 0;
        var discRays = Enumerable.Range(0, 90).Select(i => PieceSilhouette.DiscRays(pixels, geometry, i)).ToArray();
        // Some OCR dictionaries omit rare Xiangqi variants such as 俥. For an
        // unresolved visible disc, recognize the 車/馬 component to the right
        // of the person radical. Never apply this fallback to board lettering.
        // Keep the original views for flat/transparent pieces. Additional views
        // isolate the face and lettering of unresolved raised discs; a bounded
        // centre jitter handles a pixel of segmentation error after downscaling.
        for (int variant = 0; variant < 33; variant++)
        {
            var indices = Enumerable.Range(0, 90).Where(i => (requested == null || requested.Contains(i)) && (variant == 0 ||
                !IsReliable(results[i]) && (variant >= 4 ? PieceSilhouette.HasDisc(discRays[i]) :
                    !(plainGrid[i] && IsGridText(results[i]) && !PieceSilhouette.HasDisc(discRays[i]))))).ToArray();
            var pending = new Dictionary<string, Prepared>();
            void Accept(int index, Glyph glyph)
            {
                if (variant < 21 && variant % 7 is 4 or 5 && glyph.Text is not ("車" or "车" or "馬" or "马")) return;
                if (glyph.Confidence > results[index].Confidence) results[index] = glyph;
            }
            void Flush()
            {
                if (pending.Count == 0) return;
                ct.ThrowIfCancellationRequested();
                var batch = pending.ToArray();
                // The device session pads GPU batches for static compilation. CPU
                // inference only processes real rows; padding is never decoded.
                var tensor = new DenseTensor<float>(new[] { batch.Length, 3, 48, 160 });
                for (int i = 0; i < batch.Length; i++)
                    batch[i].Value.Input.CopyTo(tensor.Buffer.Span.Slice(i * 3 * 48 * 160));
                var inferenceStart = Stopwatch.GetTimestamp();
                model.Session.Run(tensor, predictions =>
                {
                    nativeInferenceMs += Stopwatch.GetElapsedTime(inferenceStart).TotalMilliseconds;
                    int steps = predictions.Dimensions[1], classes = predictions.Dimensions[2];
                    if (classes != model.Characters.Length) throw new IOException("识字模型与中文字典不匹配。");
                    ReadOnlySpan<float> values = predictions is DenseTensor<float> dense ? dense.Buffer.Span : predictions.ToArray();
                    for (int i = 0; i < batch.Length; i++)
                    {
                        var tokens = new List<(string Text, float Confidence)>(); int previous = -1;
                        for (int t = 0; t < steps; t++)
                        {
                            var row = values.Slice((i * steps + t) * classes, classes);
                            int best = 0; float score = row[0];
                            for (int c = 1; c < classes; c++) if (row[c] > score) { score = row[c]; best = c; }
                            if (best != 0 && best != previous) tokens.Add((model.Characters[best], score));
                            previous = best;
                        }
                        var glyph = Decode(tokens);
                        foreach (var index in batch[i].Value.Indices) Accept(index, glyph);
                        // Retaining 256 inputs bounds this per-request cache to
                        // 22.5 MiB of float data even when extra views are needed.
                        if (cache.Count < 256) cache[batch[i].Key] = new(batch[i].Value.Input, glyph);
                    }
                    inferenceCells += batch.Length; batches++;
                    return 0;
                }, ct);
                pending.Clear();
            }
            foreach (int index in indices)
            {
                ct.ThrowIfCancellationRequested();
                var input = new float[3 * 48 * 160];
                Fill(input, image, geometry, index, variant);
                var key = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(input.AsSpan())));
                if (cache.TryGetValue(key, out var cached) && input.AsSpan().SequenceEqual(cached.Input))
                { Accept(index, cached.Glyph); reusedCells++; }
                else if (pending.TryGetValue(key, out var prepared) && input.AsSpan().SequenceEqual(prepared.Input))
                { prepared.Indices.Add(index); reusedCells++; }
                else
                {
                    // Even a hash collision may never share a prediction.
                    while (pending.ContainsKey(key)) key += "_";
                    pending.Add(key, new(input, [index]));
                    if (pending.Count == 8) Flush();
                }
            }
            Flush();
        }
        return new(results, inferenceCells, reusedCells, batches, timer.Elapsed.TotalMilliseconds)
        { ModelLoadMs = modelLoadMs, NativeInferenceMs = nativeInferenceMs };
    }

    public static bool IsReliable(Glyph glyph) => glyph.Confidence >= .80 && glyph.Text.Length == 1 &&
        "車车俥馬马傌象相士仕將将帥帅炮砲兵卒".Contains(glyph.Text[0]);

    private static bool IsGridText(Glyph glyph) => glyph.Text.Length >= 2 && glyph.Confidence >= .05 &&
        glyph.Text.All(c => "十ＴT工I1丨一┼|".Contains(c));

    internal static bool IsPlainGrid(CapturedPixels pixels, BoardCalibration geometry, int index)
    {
        var dx=(geometry.Right-geometry.Left)/8;var dy=(geometry.Bottom-geometry.Top)/9;
        var cx=geometry.Left+index%9*dx;var cy=geometry.Top+index/9*dy;
        PiecePalette.Colour Sample(double x, double y)
        {
            int px = Math.Clamp((int)(cx + x * dx), 0, pixels.Width - 1);
            int py = Math.Clamp((int)(cy + y * dy), 0, pixels.Height - 1);
            int offset = py * pixels.RowBytes + px * 4;
            return new(pixels.Bgra[offset + 2] / 255d, pixels.Bgra[offset + 1] / 255d, pixels.Bgra[offset] / 255d);
        }
        var corners=new[]{Sample(-.44,-.44),Sample(.44,-.44),Sample(-.44,.44),Sample(.44,.44)};
        var back=new PiecePalette.Colour(corners.Average(p=>p.R),corners.Average(p=>p.G),corners.Average(p=>p.B));
        if(corners.Any(p=>back.Distance(p)>.025))return false;
        // A flat intersection with nothing outside its two grid lines needs only
        // the first OCR view. Textured boards and any possible glyph get all views.
        for(int y=-7;y<=7;y++)for(int x=-7;x<=7;x++)
        {
            if(Math.Abs(x)<2 || Math.Abs(y)<2)continue;
            var p=Sample(x*.045,y*.045);
            if(back.Distance(p)>.025)return false;
        }
        return true;
    }

    private static Glyph Decode(List<(string Text, float Confidence)> tokens)
    {
        var valid = tokens.Where(t => t.Text.Length == 1 && "車车俥馬马傌象相士仕將将帥帅炮砲兵卒".Contains(t.Text[0]))
            .GroupBy(t => t.Text).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Average(t => t.Confidence)).FirstOrDefault();
        if (valid == null) return new(string.Concat(tokens.Select(t => t.Text)), tokens.Count == 0 ? 0 : tokens.Average(t => t.Confidence) * .1);
        // Multiple distinct characters or just one plausible glyph among other text
        // are insufficient to silently authorize a board position.
        var agreement = valid.Count() / (double)Math.Max(tokens.Count, 1);
        var confidence = valid.Average(t => t.Confidence) * agreement * (valid.Count() >= 2 ? 1 : .65);
        return new(valid.Key, confidence);
    }

    private static void Fill(float[] input, SKBitmap image, BoardCalibration geometry, int index, int variant)
    {
        var dx = (geometry.Right - geometry.Left) / 8; var dy = (geometry.Bottom - geometry.Top) / 9;
        var cx = geometry.Left + index % 9 * dx; var cy = geometry.Top + index / 9 * dy;
        bool adaptive = variant >= 7;
        if (variant >= 7)
        {
            var center = DiscFaceCenter(image, cx, cy, dx, dy);
            cx = center.X; cy = center.Y;
            if (variant >= 21)
            {
                ReadOnlySpan<(double X, double Y)> offsets = [(-.04, 0), (.04, 0), (0, .04), (0, -.08), (-.04, -.04), (.04, -.04)];
                var offset = offsets[(variant - 21) / 2];
                cx += dx * offset.X; cy += dy * offset.Y;
                variant = (variant - 21) % 2 == 0 ? 1 : 6;
            }
            else
            {
                if (variant >= 14) cy -= dy * .04;
                variant %= 7;
            }
        }
        var radius = variant switch { 2 or 5 => .31, 3 => .39, _ => .35 };
        using var glyph = new SKBitmap(new SKImageInfo(44, 44));
        using (var canvas = new SKCanvas(glyph))
        {
            var source = new SKRect((float)(cx + dx * (variant is 4 or 5 ? -.06 : -radius)), (float)(cy - dy * radius),
                (float)(cx + dx * radius), (float)(cy + dy * radius));
            // A partial crop is not a complete glyph. In particular, raised
            // face/jitter views can extend beyond an otherwise valid calibration.
            // Do not let transparent padding be read as invented black strokes.
            if (source.Left < 0 || source.Top < 0 || source.Right > image.Width || source.Bottom > image.Height)
            { Array.Fill(input, 1f); return; }
            canvas.Clear(SKColors.White);
            var destination = new SKRect(0, 0, 44, 44);
            if (variant == 6)
            {
                // Sparse strokes (notably 士) alias when a small screenshot is
                // enlarged with nearest sampling. Keep the original views, then
                // retry unresolved discs with a different sampling filter.
                using var original = SKImage.FromBitmap(image);
                canvas.DrawImage(original, source, destination, new SKSamplingOptions(SKFilterMode.Linear));
            }
            else canvas.DrawBitmap(image, source, destination);
        }
        var pixels = new List<PiecePalette.Colour>();
        for (int y = 0; y < 44; y++) for (int x = 0; x < 44; x++)
        {
            if ((x-21.5)*(x-21.5)+(y-21.5)*(y-21.5)>21*21) continue;
            var p = glyph.GetPixel(x,y); pixels.Add(new(p.Red/255d,p.Green/255d,p.Blue/255d));
        }
        var colours = variant == 0 ? [] : PiecePalette.Cluster(pixels,2);
        int background = 0;
        if (colours.Length == 2)
        {
            var votes = new int[2];
            for (int y=0;y<44;y++) for(int x=0;x<44;x++)
            {
                double radiusSquared=(x-21.5)*(x-21.5)+(y-21.5)*(y-21.5);
                if (radiusSquared is < 256 or > 441) continue;
                var p=glyph.GetPixel(x,y); var colour=new PiecePalette.Colour(p.Red/255d,p.Green/255d,p.Blue/255d);
                votes[colours[0].Distance(colour) < colours[1].Distance(colour) ? 0 : 1]++;
            }
            background = votes[0] >= votes[1] ? 0 : 1;
        }
        if (adaptive && variant != 0 && colours.Length == 2)
            NormalizeInk(glyph, colours, background, variant is 4 or 5, radius, variant is 3 or 6);
        var values = input.AsSpan();
        int plane = 48*160;
        for (int y = 0; y < 48; y++) for (int x = 0; x < 160; x++)
        {
            int local = x % 52 - 4;
            var p = y is >= 2 and < 46 && local is >= 0 and < 44 && x < 156
                ? glyph.GetPixel(local, y - 2) : SKColors.White;
            if (variant != 0 && !adaptive && y is >= 2 and < 46 && local is >= 0 and < 44 && x < 156)
            {
                // Segment in RGB, with polarity inferred from the surrounding disc.
                // Equal-luminance blue/yellow or green/pink still retain their glyphs.
                var colour = new PiecePalette.Colour(p.Red/255d,p.Green/255d,p.Blue/255d);
                var ink = colours.Length == 2 && colours[background].Distance(colour) > colours[1-background].Distance(colour);
                var value = (byte)(ink ? 0 : 255);
                if (variant == 3 && colours.Length == 2)
                {
                    var backDistance = colours[background].Distance(colour);
                    var inkDistance = colours[1-background].Distance(colour);
                    value = (byte)(255*inkDistance/Math.Max(.001,backDistance+inkDistance));
                }
                if ((local - 21.5) * (local - 21.5) + (y - 23.5) * (y - 23.5) > 21.5 * 21.5) value = 255;
                p = new(value, value, value);
            }
            int at = y*160+x;
            values[at] = p.Blue / 127.5f - 1;
            values[at+plane] = p.Green / 127.5f - 1;
            values[at+2*plane] = p.Red / 127.5f - 1;
        }
    }

    // A raised disc's face can sit above its grid intersection. Locate the
    // connected face fill by its own palette instead of assuming a board colour
    // or cropping glyphs around the grid centre. If the fill merges into the
    // board (flat/transparent themes), retain the original centred OCR views.
    private static (double X, double Y) DiscFaceCenter(SKBitmap image, double cx, double cy, double dx, double dy)
    {
        PiecePalette.Colour Colour(int x, int y)
        {
            var p = image.GetPixel(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));
            return new(p.Red / 255d, p.Green / 255d, p.Blue / 255d);
        }
        var core = new List<PiecePalette.Colour>();
        for (int y = -10; y <= 10; y++) for (int x = -10; x <= 10; x++)
            if (x * x + y * y <= 100) core.Add(Colour((int)(cx + x * dx * .028), (int)(cy + y * dy * .028)));
        var palette = PiecePalette.Cluster(core, 2);
        if (palette.Length != 2 || palette[0].Distance(palette[1]) < .04) return (cx, cy);
        int left = (int)(cx - dx * .48), top = (int)(cy - dy * .48);
        int width = (int)(dx * .96) + 1, height = (int)(dy * .96) + 1;
        var fill = new bool[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            var colour = Colour(left + x, top + y);
            fill[y * width + x] = palette[0].Distance(colour) < palette[1].Distance(colour);
        }
        var visited = new bool[fill.Length];
        (int Count, int Left, int Top, int Right, int Bottom) best = default;
        for (int start = 0; start < fill.Length; start++)
        {
            if (visited[start]) continue;
            var group = fill[start];
            var queue = new Queue<int>(); queue.Enqueue(start); visited[start] = true;
            int count = 0, minX = width, minY = height, maxX = 0, maxY = 0;
            while (queue.TryDequeue(out var at))
            {
                int x = at % width, y = at / width; count++;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                void Visit(int next) { if (fill[next] == group && !visited[next]) { visited[next] = true; queue.Enqueue(next); } }
                if (x > 0) Visit(at - 1); if (x + 1 < width) Visit(at + 1);
                if (y > 0) Visit(at - width); if (y + 1 < height) Visit(at + width);
            }
            double sx = maxX - minX, sy = maxY - minY;
            double candidateX = left + (minX + maxX) / 2d, candidateY = top + (minY + maxY) / 2d;
            if (sx >= dx * .4 && sx <= dx * .86 && sy >= dy * .4 && sy <= dy * .86 &&
                Math.Abs(candidateX - cx) <= dx * .16 && Math.Abs(candidateY - cy) <= dy * .20 && count > best.Count)
                best = (count, minX, minY, maxX, maxY);
        }
        double spanX = best.Right - best.Left, spanY = best.Bottom - best.Top;
        double faceX = left + (best.Left + best.Right) / 2d, faceY = top + (best.Top + best.Bottom) / 2d;
        if (spanX < dx * .4 || spanX > dx * .86 || spanY < dy * .4 || spanY > dy * .86 ||
            Math.Abs(faceX - cx) > dx * .16 || Math.Abs(faceY - cy) > dy * .20) return (cx, cy);
        return (faceX, faceY);
    }

    private static void NormalizeInk(SKBitmap glyph, PiecePalette.Colour[] colours, int background, bool component, double radius, bool soft)
    {
        var ink = new bool[44 * 44]; var radii = new double[ink.Length];
        int minX = 44, minY = 44, maxX = 0, maxY = 0;
        for(int y=0;y<44;y++) for(int x=0;x<44;x++)
        {
            // Remove the engraved rim using distance from the face centre. For
            // the optional 車/馬 component, retain its original source coordinates.
            double nx = component ? -.06 + (x + .5) / 44 * (radius + .06) : ((x + .5) / 44 * 2 - 1) * radius;
            double ny = ((y + .5) / 44 * 2 - 1) * radius;
            var p = glyph.GetPixel(x,y); var colour = new PiecePalette.Colour(p.Red/255d,p.Green/255d,p.Blue/255d);
            radii[y * 44 + x] = Math.Sqrt(nx * nx + ny * ny);
            if (radii[y * 44 + x] > .38 || colours[background].Distance(colour) <= colours[1-background].Distance(colour)) continue;
            ink[y * 44 + x] = true;
        }
        var seen = new bool[ink.Length]; var removed = new bool[ink.Length];
        for(int start=0;start<ink.Length;start++)
        {
            if (!ink[start] || seen[start]) continue;
            var queue = new Queue<int>(); var componentPixels = new List<int>();
            queue.Enqueue(start); seen[start]=true;
            while(queue.TryDequeue(out var at))
            {
                componentPixels.Add(at); int x=at%44,y=at/44;
                for(int oy=-1;oy<=1;oy++)for(int ox=-1;ox<=1;ox++)
                {
                    int xx=x+ox,yy=y+oy;if(xx<0||xx>=44||yy<0||yy>=44)continue;
                    int next=yy*44+xx;if(ink[next]&&!seen[next]){seen[next]=true;queue.Enqueue(next);}
                }
            }
            // Engraved circular rims consist of peripheral components. Preserve
            // central strokes and detached dots rather than erasing a fixed crop.
            if(componentPixels.Count>5 && componentPixels.Count(at=>radii[at]>.28)>componentPixels.Count*.85)
                foreach(var at in componentPixels){ink[at]=false;removed[at]=true;}
        }
        for(int y=0;y<44;y++)for(int x=0;x<44;x++)if(ink[y*44+x])
        {minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);}
        if(minX>maxX || minY>maxY) return;
        if (soft)
        {
            minX = Math.Max(0, minX - 1); minY = Math.Max(0, minY - 1);
            maxX = Math.Min(43, maxX + 1); maxY = Math.Min(43, maxY + 1);
        }
        using var mask = new SKBitmap(44,44);
        for(int y=0;y<44;y++)for(int x=0;x<44;x++)
        {
            byte value=255;
            bool keep = ink[y * 44 + x];
            if (soft && !keep && !removed[y * 44 + x] && radii[y * 44 + x] <= .38)
            {
                // Retain antialiasing around accepted strokes, including the
                // background side of the threshold; erased rim stays erased.
                for (int oy = -1; oy <= 1 && !keep; oy++) for (int ox = -1; ox <= 1; ox++)
                {
                    int xx = x + ox, yy = y + oy;
                    if (xx >= 0 && xx < 44 && yy >= 0 && yy < 44 && ink[yy * 44 + xx]) { keep = true; break; }
                }
            }
            if(keep)
            {
                var p=glyph.GetPixel(x,y);var colour=new PiecePalette.Colour(p.Red/255d,p.Green/255d,p.Blue/255d);
                var back=colours[background].Distance(colour);var fore=colours[1-background].Distance(colour);
                value=soft?(byte)(255*fore/Math.Max(.001,back+fore)):(byte)0;
            }
            mask.SetPixel(x,y,new(value,value,value));
        }
        var width=maxX-minX+1;var height=maxY-minY+1;
        var aspect = component ? (radius + .06) / (radius * 2) : 1;
        var scale=Math.Min(36d / (width * aspect), 38d / height);
        var w=(float)(width*aspect*scale); var h=(float)(height*scale);
        using var canvas=new SKCanvas(glyph);canvas.Clear(SKColors.White);
        using var original=SKImage.FromBitmap(mask);
        canvas.DrawImage(original,new SKRect(minX,minY,maxX+1,maxY+1),SKRect.Create((44-w)/2,(44-h)/2,w,h),new SKSamplingOptions(SKFilterMode.Linear));
    }
}
