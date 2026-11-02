using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TutorialOverlay;

/// <summary>One recognised word, in the coordinates of the image that was read.</summary>
internal readonly record struct OcrWord(string Text, RectangleF Rect);

/// <summary>A line of recognised words.</summary>
internal sealed class OcrLine
{
    public readonly List<OcrWord> Words = new();
    public string Text => string.Join(" ", Words.Select(w => w.Text));
    public RectangleF Bounds => Union(Words.Select(w => w.Rect));

    public static RectangleF Union(IEnumerable<RectangleF> rects)
    {
        RectangleF? all = null;
        foreach (var r in rects) all = all is RectangleF a ? RectangleF.Union(a, r) : r;
        return all ?? RectangleF.Empty;
    }
}

/// <summary>Where a piece of text was found.</summary>
internal readonly record struct TextMatch(string Text, RectangleF Rect, float Score);

/// <summary>
/// Text recognition with the OCR engine built into Windows (Windows.Media.Ocr), called through raw WinRT
/// interfaces — no projection assembly, so the exe stays small. Interface ids and slot numbers come from
/// Windows.winmd (UnionMetadata 10.0.26100.0).
/// </summary>
internal static unsafe class Ocr
{
    // ---- WinRT plumbing (combase.dll)
    [DllImport("combase.dll")] private static extern int RoInitialize(int type);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, int len, out IntPtr h);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr h);
    [DllImport("combase.dll")] private static extern char* WindowsGetStringRawBuffer(IntPtr h, out uint len);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    private static readonly Guid IID_IOcrEngineStatics = new("5BFFA85A-3384-3540-9940-699120D428A8");
    private static readonly Guid IID_ISoftwareBitmapStatics = new("DF0385DB-672F-4A9D-806E-C2442F343E86");
    private static readonly Guid IID_IBufferFactory = new("71AF914D-C10F-484B-BC50-14BC623B3A27");
    private static readonly Guid IID_IBufferByteAccess = new("905A0FEF-BC53-11DF-8C49-001E4FC686DA");
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    private static readonly Guid IID_ILanguageFactory = new("9B0252AC-0C27-44F8-B792-9793FB66C63E");
    private const int BitmapPixelFormat_Bgra8 = 87;

    [ThreadStatic] private static bool _roInit;
    private static readonly ConcurrentDictionary<string, IntPtr> Engines = new();   // OcrEngine per language ("" = user profile)
    private static readonly object Gate = new();

    private static void* Slot(IntPtr obj, int i) => (*(void***)obj)[i];
    private static void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(p, 2))(p); }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"OCR: {what} failed (0x{hr:X8})");
    }

    private static IntPtr QueryInterface(IntPtr p, Guid iid)
    {
        IntPtr res;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(p, 0))(p, &iid, &res), "QueryInterface");
        return res;
    }

    private static IntPtr HString(string s)
    {
        Check(WindowsCreateString(s, s.Length, out var h), "WindowsCreateString");
        return h;
    }

    private static string ReadHString(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var p = WindowsGetStringRawBuffer(h, out uint len);
        var s = new string(p, 0, (int)len);
        WindowsDeleteString(h);
        return s;
    }

    private static IntPtr Factory(string cls, Guid iid)
    {
        if (!_roInit) { RoInitialize(1); _roInit = true; }   // MTA; harmless if the thread is already initialised
        var h = HString(cls);
        try
        {
            Check(RoGetActivationFactory(h, ref iid, out var f), "activation of " + cls);
            return f;
        }
        finally { WindowsDeleteString(h); }
    }

    /// <summary>Languages the OCR engine can read on this machine (BCP-47 tags).</summary>
    public static List<string> Languages()
    {
        var res = new List<string>();
        var stat = Factory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics);
        try
        {
            IntPtr vec;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(stat, 7))(stat, &vec), "AvailableRecognizerLanguages");
            try
            {
                foreach (var lang in VectorItems(vec))
                {
                    IntPtr tag;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(lang, 6))(lang, &tag) >= 0) res.Add(ReadHString(tag));
                    Release(lang);
                }
            }
            finally { Release(vec); }
        }
        finally { Release(stat); }
        return res;
    }

    private static IntPtr Engine(string? lang)
    {
        var key = lang ?? "";
        if (Engines.TryGetValue(key, out var e)) return e;
        lock (Gate)
        {
            if (Engines.TryGetValue(key, out e)) return e;
            var stat = Factory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics);
            try
            {
                IntPtr engine;
                if (string.IsNullOrEmpty(lang))
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(stat, 10))(stat, &engine), "TryCreateFromUserProfileLanguages");
                else
                {
                    var lf = Factory("Windows.Globalization.Language", IID_ILanguageFactory);
                    IntPtr language;
                    var tag = HString(lang);
                    try { Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(lf, 6))(lf, tag, &language), "CreateLanguage"); }
                    finally { WindowsDeleteString(tag); Release(lf); }
                    try { Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(stat, 9))(stat, language, &engine), "TryCreateFromLanguage"); }
                    finally { Release(language); }
                }
                if (engine == IntPtr.Zero)
                    throw new InvalidOperationException($"OCR: no recognizer for '{(string.IsNullOrEmpty(lang) ? "user profile languages" : lang)}'; " +
                                                        $"installed: {string.Join(", ", Languages())} (add one in Windows Settings → Language)");
                Engines[key] = engine;
                return engine;
            }
            finally { Release(stat); }
        }
    }

    /// <summary>All items of an IVectorView; the caller releases each one.</summary>
    private static List<IntPtr> VectorItems(IntPtr vec)
    {
        uint n;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(vec, 7))(vec, &n), "IVectorView.Size");
        var items = new List<IntPtr>((int)n);
        for (uint i = 0; i < n; i++)
        {
            IntPtr item;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(vec, 6))(vec, i, &item), "IVectorView.GetAt");
            items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// Reads the text in <paramref name="img"/>. Small UI text is read better enlarged, so the image is scaled
    /// up to 2× (within the engine's size limit); rectangles come back in <paramref name="img"/>'s pixels.
    /// </summary>
    public static List<OcrLine> Read(Bitmap img, string? lang = null)
    {
        var engine = Engine(lang);
        const int maxDim = 2600;    // conservative; OcrEngine.MaxImageDimension is 10000 on current Windows
        float scale = Math.Min(2f, maxDim / (float)Math.Max(img.Width, img.Height));
        int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
        using var src = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(src))
        {
            g.InterpolationMode = scale >= 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(img, 0, 0, w, h);
        }

        IntPtr buffer = IntPtr.Zero, sbmp = IntPtr.Zero, op = IntPtr.Zero, info = IntPtr.Zero, result = IntPtr.Zero, lines = IntPtr.Zero;
        try
        {
            // pixels → IBuffer
            uint size = (uint)(w * h * 4);
            var bf = Factory("Windows.Storage.Streams.Buffer", IID_IBufferFactory);
            try { Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(bf, 6))(bf, size, &buffer), "Buffer.Create"); }
            finally { Release(bf); }
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(buffer, 8))(buffer, size), "IBuffer.Length");
            var access = QueryInterface(buffer, IID_IBufferByteAccess);
            try
            {
                byte* dst;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, int>)Slot(access, 3))(access, &dst), "IBufferByteAccess.Buffer");
                var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy((byte*)data.Scan0 + y * data.Stride, dst + y * w * 4, w * 4, w * 4);
                }
                finally { src.UnlockBits(data); }
            }
            finally { Release(access); }

            // IBuffer → SoftwareBitmap (Bgra8)
            var sbs = Factory("Windows.Graphics.Imaging.SoftwareBitmap", IID_ISoftwareBitmapStatics);
            try { Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, int, IntPtr*, int>)Slot(sbs, 9))(sbs, buffer, BitmapPixelFormat_Bgra8, w, h, &sbmp), "SoftwareBitmap.CreateCopyFromBuffer"); }
            finally { Release(sbs); }

            // RecognizeAsync — wait for it on this (worker) thread
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(engine, 6))(engine, sbmp, &op), "RecognizeAsync");
            info = QueryInterface(op, IID_IAsyncInfo);
            var until = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                int status;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 7))(info, &status), "IAsyncInfo.Status");
                if (status == 1) break;                                    // Completed
                if (status == 2) throw new InvalidOperationException("OCR: cancelled");
                if (status == 3)
                {
                    int err;
                    ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 8))(info, &err);
                    throw new InvalidOperationException($"OCR: recognition failed (0x{err:X8})");
                }
                if (DateTime.UtcNow > until) throw new TimeoutException("OCR: no result in 15 s");
                System.Threading.Thread.Sleep(5);
            }
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(op, 8))(op, &result), "GetResults");

            // OcrResult.Lines → words with their rectangles
            var res = new List<OcrLine>();
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(result, 6))(result, &lines), "OcrResult.Lines");
            foreach (var line in VectorItems(lines))
            {
                try
                {
                    IntPtr words;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(line, 6))(line, &words), "OcrLine.Words");
                    try
                    {
                        var ol = new OcrLine();
                        foreach (var word in VectorItems(words))
                        {
                            try
                            {
                                RectangleF r;
                                IntPtr text;
                                Check(((delegate* unmanaged[Stdcall]<IntPtr, RectangleF*, int>)Slot(word, 6))(word, &r), "OcrWord.BoundingRect");
                                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(word, 7))(word, &text), "OcrWord.Text");
                                ol.Words.Add(new OcrWord(ReadHString(text), new RectangleF(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale)));
                            }
                            finally { Release(word); }
                        }
                        if (ol.Words.Count > 0) res.Add(ol);
                    }
                    finally { Release(words); }
                }
                finally { Release(line); }
            }
            return res;
        }
        finally
        {
            Release(lines); Release(result); Release(info); Release(op); Release(sbmp); Release(buffer);
        }
    }

    // =========================================================== matching

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ").Trim();

    /// <summary>
    /// Occurrences of <paramref name="query"/> in the recognised lines, best first. match: contains (default) |
    /// exact (the whole line) | regex. OCR misreads a letter now and then (l/I, 0/O), so contains/exact also accept
    /// a near match: up to one wrong character per 6 (score = share of wrong characters, 0 = exact).
    /// </summary>
    public static List<TextMatch> Find(List<OcrLine> lines, string query, string match = "contains")
    {
        var res = new List<TextMatch>();
        var q = Norm(query);
        if (q.Length == 0) return res;
        foreach (var line in lines)
        {
            // line text with each character mapped back to its word
            var sb = new StringBuilder();
            var owner = new List<int>();
            for (int i = 0; i < line.Words.Count; i++)
            {
                if (i > 0) { sb.Append(' '); owner.Add(-1); }
                var wt = Norm(line.Words[i].Text);
                sb.Append(wt);
                for (int k = 0; k < wt.Length; k++) owner.Add(i);
            }
            var text = sb.ToString();
            RectangleF Span(int start, int len)
            {
                var idx = owner.Skip(start).Take(len).Where(o => o >= 0).Distinct();
                return OcrLine.Union(idx.Select(i => line.Words[i].Rect));
            }
            string Original(int start, int len)
            {
                var idx = owner.Skip(start).Take(len).Where(o => o >= 0).Distinct();
                return string.Join(" ", idx.Select(i => line.Words[i].Text));
            }

            if (match == "regex")
            {
                foreach (Match m in Regex.Matches(text, query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    if (m.Length > 0) res.Add(new TextMatch(Original(m.Index, m.Length), Span(m.Index, m.Length), 0));
                continue;
            }
            int tol = q.Length / 6;
            if (match == "exact")
            {
                int d = Distance(text, q);
                if (d <= tol) res.Add(new TextMatch(line.Text, line.Bounds, d / (float)q.Length));
                continue;
            }
            int at = text.IndexOf(q, StringComparison.Ordinal);
            if (at >= 0)
            {
                while (at >= 0)
                {
                    res.Add(new TextMatch(Original(at, q.Length), Span(at, q.Length), 0));
                    at = text.IndexOf(q, at + q.Length, StringComparison.Ordinal);
                }
                continue;
            }
            if (tol == 0 || text.Length < q.Length - tol) continue;
            // near match: the best window of the line
            int best = int.MaxValue, bestAt = -1, bestLen = 0;
            for (int len = Math.Max(1, q.Length - tol); len <= q.Length + tol; len++)
                for (int s = 0; s + len <= text.Length; s++)
                {
                    int d = Distance(text.Substring(s, len), q);
                    if (d < best) { best = d; bestAt = s; bestLen = len; }
                }
            if (bestAt >= 0 && best <= tol) res.Add(new TextMatch(Original(bestAt, bestLen), Span(bestAt, bestLen), best / (float)q.Length));
        }
        return res.OrderBy(m => m.Score).ToList();
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
