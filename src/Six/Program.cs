using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using JeremyAnsel.ColorQuant;
using SkiaSharp;
using Microsoft.Win32.SafeHandles;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: SixelViewer <folder>");
    return 1;
}

string folder = Path.GetFullPath(args[0]);
if (!Directory.Exists(folder))
{
    Console.Error.WriteLine($"Directory not found: {folder}");
    return 1;
}

var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tiff", ".tif" };

var allFiles = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
    .Where(f => extensions.Contains(Path.GetExtension(f)))
    .ToArray();
Array.Sort(allFiles, StringComparer.OrdinalIgnoreCase);

var files = (string[])allFiles.Clone();
ApplySort(files, allFiles, SortMode.Random, groupByFolder: false);

static void Shuffle(string[] arr)
{
    for (int i = arr.Length - 1; i > 0; i--)
    {
        int j = Random.Shared.Next(i + 1);
        (arr[i], arr[j]) = (arr[j], arr[i]);
    }
}

static void SortItems(string[] items, SortMode sortMode)
{
    switch (sortMode)
    {
        case SortMode.Random:
            Shuffle(items);
            break;
        case SortMode.Name:
            Array.Sort(items, StringComparer.OrdinalIgnoreCase);
            break;
        case SortMode.NameDesc:
            Array.Sort(items, StringComparer.OrdinalIgnoreCase);
            Array.Reverse(items);
            break;
        case SortMode.Date:
            var times = new Dictionary<string, DateTime>(items.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var f in items) times[f] = File.GetLastWriteTimeUtc(f);
            Array.Sort(items, (a, b) => times[b].CompareTo(times[a]));
            break;
        case SortMode.DateAsc:
            var timesAsc = new Dictionary<string, DateTime>(items.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var f in items) timesAsc[f] = File.GetLastWriteTimeUtc(f);
            Array.Sort(items, (a, b) => timesAsc[a].CompareTo(timesAsc[b]));
            break;
    }
}

static void ApplySort(string[] files, string[] allFiles, SortMode sortMode, bool groupByFolder)
{
    if (groupByFolder)
    {
        var groups = allFiles
            .GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        int pos = 0;
        foreach (var group in groups)
        {
            var items = group.ToArray();
            SortItems(items, sortMode);
            Array.Copy(items, 0, files, pos, items.Length);
            pos += items.Length;
        }
    }
    else
    {
        Array.Copy(allFiles, files, allFiles.Length);
        SortItems(files, sortMode);
    }
}

if (files.Length == 0)
{
    Console.Error.WriteLine("No image files found in the specified folder.");
    return 1;
}

int index = 0;
bool quit = false;
var sortMode = SortMode.Random;
bool groupByFolder = false;
bool autoAdvance = false;
var sixelCache = new Dictionary<string, CachedFrame>();
var preloadLock = new object();

// Alternative image cycling: same filename in different directories
int altIndex = -1; // -1 means showing the playlist file itself
string? altBasePath = null; // the playlist entry we're showing alternatives for
string[] altFiles = Array.Empty<string>(); // all files with same filename

// Hide cursor, switch to alternate screen
Console.Write("\x1b[?25l\x1b[?1049h");

try
{
    // Show window metadata on startup
    GetConsoleSizePixels(out int dbgW, out int dbgH, out int dbgCellW, out int dbgCellH);
    //Console.WriteLine($" Console: {Console.WindowWidth}x{Console.WindowHeight} chars, {Console.BufferWidth}x{Console.BufferHeight} buffer");
    //Console.WriteLine($" CSI 14t: {dbgW}x{dbgH} px, cell: {dbgCellW}x{dbgCellH} px");
    //Console.WriteLine($" First image: {files[0]}");
    //{
        //using var probe = SKBitmap.Decode(files[0]);
        //Console.WriteLine($" Image size: {probe.Width}x{probe.Height}");
    //}
    //{
        //int availH = dbgH - 2 * dbgCellH;
        //availH = availH / 6 * 6;
        //Console.WriteLine($" Avail height: {availH} px");
    //}

    // Get the actual file path to display (considering alt cycling)
    string DisplayPath() => (altIndex >= 0 && altBasePath == files[index]) ? altFiles[altIndex] : files[index];

    void ResetAlt() { altIndex = -1; altBasePath = null; altFiles = Array.Empty<string>(); }

    void Redraw()
    {
        var displayPath = DisplayPath();
        ShowImage(displayPath, files.Length, index, sortMode, groupByFolder, autoAdvance, sixelCache, preloadLock, folder, altIndex >= 0 ? $" ALT {altIndex + 1}/{altFiles.Length}" : null);
        // Preload neighbors based on playlist position
        GetConsoleSizePixels(out int cW, out int cH, out int cellW, out int cellH);
        int availH = cH - 1 * cellH - 2 * cellH;
        if (availH < cellH) availH = cellH;
        availH = availH / 6 * 6;
        int availW = cW - 2 * cellW;
        TrimAndPreload(files, index, availW, availH, cellW, cellH, sixelCache, preloadLock);
    }

    /// <summary>
    /// Replaces every alternate with an upscaled version, in one upscayl run.
    /// </summary>
    /// <remarks>
    /// <para>Synchronous, unlike its counterpart in the GUI. There is no frame to update from a
    /// background thread here -- the key loop is sitting in ReadKey -- and drawing over the screen
    /// from somewhere else while it does would scramble the picture on it.</para>
    /// <para>The alternates all share a filename, which is what makes them alternates, so they are
    /// staged under indexed names rather than colliding in one folder.</para>
    /// </remarks>
    void UpscaleAlternatesAsBatch(string[] alternates, string ext)
    {
        if (alternates.Length == 0)
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "six_batch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        var mapping = new Dictionary<string, string>(); // staged path -> where it came from
        for (int i = 0; i < alternates.Length; i++)
        {
            var staged = Path.Combine(tempDir, $"{i}{ext}");
            File.Move(alternates[i], staged);
            mapping[staged] = alternates[i];
        }

        ShowStatus($" Batch upscaling {alternates.Length} alternate(s)...");

        var psi = new ProcessStartInfo("upscayl", $"-i \"{tempDir}\" -o \"{tempDir}\" -n upscayl-lite-4x -s 2")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi)?.WaitForExit();

        bool everythingCameBack = true;
        foreach (var (staged, home) in mapping)
        {
            if (File.Exists(staged))
            {
                File.Move(staged, home);
                lock (preloadLock) { sixelCache.Remove(home); }
            }
            else
            {
                everythingCameBack = false;
            }
        }

        // Only sweep the staging folder up when every picture is back where it belongs. These files
        // were MOVED out of the library, so deleting the folder after a failed run would take the
        // originals with it -- and a photo is not something to lose to a tidy-up.
        if (everythingCameBack)
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
        else
        {
            ShowStatus($" Some alternates did not come back; left in {tempDir}  (press any key)");
            Console.ReadKey(true);
        }
    }

    /// <summary>
    /// Upscales each alternate on its own, all at once, without waiting for any of them.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the playlist arrays -- each task replaces one file in place -- so it is
    /// safe to leave running while the key loop carries on.
    /// </remarks>
    void UpscaleAlternatesInParallel(string[] alternates, string ext)
    {
        foreach (var alternate in alternates)
        {
            var altPath = alternate;
            var staged = Path.Combine(Path.GetDirectoryName(altPath)!,
                                      Path.GetFileNameWithoutExtension(altPath) + "_tmp_upscale" + ext);
            _ = Task.Run(() =>
            {
                var psi = new ProcessStartInfo("upscayl", $"-i \"{altPath}\" -o \"{staged}\" -n upscayl-lite-4x -s 2")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi)?.WaitForExit();

                // Staged alongside rather than over the top, so a failed run leaves the original be.
                if (File.Exists(staged))
                {
                    File.Delete(altPath);
                    File.Move(staged, altPath);
                    lock (preloadLock) { sixelCache.Remove(altPath); }
                }
            });
        }
    }

    Redraw();

    var lastAdvance = Environment.TickCount64;
    while (!quit)
    {
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.RightArrow:
                    if (index < files.Length - 1) { index++; ResetAlt(); Redraw(); lastAdvance = Environment.TickCount64; }
                    break;
                case ConsoleKey.LeftArrow:
                    if (index > 0) { index--; ResetAlt(); Redraw(); lastAdvance = Environment.TickCount64; }
                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.UpArrow:
                    {
                        string currentFile = files[index];
                        string currentName = Path.GetFileName(currentFile);
                        // Build alt list if not already for this file
                        if (altBasePath != currentFile)
                        {
                            altFiles = allFiles.Where(f => Path.GetFileName(f).Equals(currentName, StringComparison.OrdinalIgnoreCase)).ToArray();
                            altBasePath = currentFile;
                            altIndex = -1;
                        }
                        if (altFiles.Length > 1)
                        {
                            int cur = altIndex >= 0 ? altIndex : Array.IndexOf(altFiles, currentFile);
                            if (key.Key == ConsoleKey.DownArrow)
                                altIndex = (cur + 1) % altFiles.Length;
                            else
                                altIndex = (cur - 1 + altFiles.Length) % altFiles.Length;
                            Redraw();
                        }
                    }
                    break;
                case ConsoleKey.O:
                    // What is on screen, which is not the playlist entry while an alternate is being
                    // cycled through. Opening a different picture from the one being looked at is a
                    // surprise every time.
                    Process.Start(new ProcessStartInfo(DisplayPath()) { UseShellExecute = true });
                    break;
                case ConsoleKey.U:
                    {
                        var src = files[index];
                        var ext = Path.GetExtension(src);
                        var dst = Path.Combine(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + "_resized" + ext);

                        // Already upscaled. Running it again would spend minutes to overwrite the
                        // result with the same thing.
                        if (File.Exists(dst))
                            break;

                        RunUpscale(src, dst);

                        if (File.Exists(dst))
                        {
                            // Insert into file arrays and navigate to it
                            allFiles = allFiles.Append(dst).ToArray();
                            Array.Sort(allFiles, StringComparer.OrdinalIgnoreCase);
                            files = files.Take(index + 1).Append(dst).Concat(files.Skip(index + 1)).ToArray();
                            index++;
                            ResetAlt();
                            lock (preloadLock) { sixelCache.Clear(); }
                            Redraw();
                        }
                    }
                    break;
                case ConsoleKey.R:
                    {
                        var cur = files[index];
                        var nameNoExt = Path.GetFileNameWithoutExtension(cur);
                        if (nameNoExt.EndsWith("_resized"))
                        {
                            bool shiftR = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
                            var ext = Path.GetExtension(cur);
                            var baseName = nameNoExt[..^"_resized".Length];
                            var original = Path.Combine(Path.GetDirectoryName(cur)!, baseName + ext);
                            var originalFileName = baseName + ext;

                            lock (preloadLock) { sixelCache.Remove(cur); sixelCache.Remove(original); }

                            // Step 1: the resized file takes the original's place.
                            File.Delete(original);
                            File.Move(cur, original);
                            allFiles = allFiles.Where(f => f != cur).ToArray();
                            files = files.Where(f => f != cur).ToArray();

                            // Step 2: give every same-named file in the other folders the same
                            // treatment. Accepting an upscale for one copy of a picture and leaving
                            // the copies alone is almost never what was meant.
                            var alternates = allFiles.Where(f => f != original
                                && Path.GetFileName(f).Equals(originalFileName, StringComparison.OrdinalIgnoreCase)).ToArray();

                            if (shiftR)
                                UpscaleAlternatesInParallel(alternates, ext);
                            else
                                UpscaleAlternatesAsBatch(alternates, ext);

                            // Stay put. The _resized entry has just left the list, so this index is
                            // already the NEXT picture; going back to the original would mean staring
                            // at the one just replaced.
                            if (index >= files.Length) index = files.Length - 1;
                            ResetAlt();
                            Redraw();
                        }
                    }
                    break;
                case ConsoleKey.Spacebar:
                    autoAdvance = !autoAdvance;
                    lastAdvance = Environment.TickCount64;
                    Redraw();
                    break;
                case ConsoleKey.Delete:
                    {
                        string fileToDel = files[index];
                        bool isResized = Path.GetFileNameWithoutExtension(fileToDel).EndsWith("_resized");
                        if (isResized)
                        {
                            Console.WriteLine($"Deleted: {fileToDel}");
                            File.Delete(fileToDel);
                        }
                        else
                        {
                            foreach(var file in allFiles.Where(f => Path.GetFileName(f) == Path.GetFileName(fileToDel)))
                            {
                                Console.WriteLine($"Deleted: {file}");
                                File.Delete(file);
                            }
                        }
                        Console.ReadKey(true);
                        // Remove from both arrays
                        allFiles = allFiles.Where(f => f != fileToDel).ToArray();
                        files = files.Where(f => f != fileToDel).ToArray();
                        lock (preloadLock) { sixelCache.Remove(fileToDel); }
                        ResetAlt();
                        if (files.Length == 0) { quit = true; break; }
                        if (index >= files.Length) index = files.Length - 1;
                        Redraw();
                        lastAdvance = Environment.TickCount64;
                    }
                    break;
                case ConsoleKey.Q:
                    quit = true;
                    break;
                case ConsoleKey.Escape:
                    quit = true;
                    break;
                case ConsoleKey.Home:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                    {
                        index = 0;
                    }
                    else
                    {
                        // Jump to first image in current folder group (per current sort order)
                        string curDir = Path.GetDirectoryName(files[index])!;
                        for (int i = 0; i < files.Length; i++)
                        {
                            if (Path.GetDirectoryName(files[i])!.Equals(curDir, StringComparison.OrdinalIgnoreCase))
                            { index = i; break; }
                        }
                    }
                    ResetAlt(); Redraw(); lastAdvance = Environment.TickCount64;
                    break;
                case ConsoleKey.End:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                    {
                        index = files.Length - 1;
                    }
                    else
                    {
                        // Jump to last image in current folder group (per current sort order)
                        string curDir = Path.GetDirectoryName(files[index])!;
                        for (int i = files.Length - 1; i >= 0; i--)
                        {
                            if (Path.GetDirectoryName(files[i])!.Equals(curDir, StringComparison.OrdinalIgnoreCase))
                            { index = i; break; }
                        }
                    }
                    ResetAlt(); Redraw(); lastAdvance = Environment.TickCount64;
                    break;
                case ConsoleKey.PageDown:
                    {
                        // Jump to first file of next folder group
                        string curDir = Path.GetDirectoryName(files[index])!;
                        for (int i = index + 1; i < files.Length; i++)
                        {
                            if (!Path.GetDirectoryName(files[i])!.Equals(curDir, StringComparison.OrdinalIgnoreCase))
                            { index = i; break; }
                        }
                        ResetAlt(); Redraw(); lastAdvance = Environment.TickCount64;
                    }
                    break;
                case ConsoleKey.PageUp:
                    {
                        // Jump to first file of previous folder group
                        string curDir = Path.GetDirectoryName(files[index])!;
                        // First, find the start of the current group
                        int groupStart = index;
                        while (groupStart > 0 && Path.GetDirectoryName(files[groupStart - 1])!.Equals(curDir, StringComparison.OrdinalIgnoreCase))
                            groupStart--;
                        if (groupStart > 0)
                        {
                            // Move into the previous group, then find its start
                            string prevDir = Path.GetDirectoryName(files[groupStart - 1])!;
                            int prevStart = groupStart - 1;
                            while (prevStart > 0 && Path.GetDirectoryName(files[prevStart - 1])!.Equals(prevDir, StringComparison.OrdinalIgnoreCase))
                                prevStart--;
                            index = prevStart;
                        }
                        ResetAlt(); Redraw(); lastAdvance = Environment.TickCount64;
                    }
                    break;
                case ConsoleKey.G:
                    {
                        string currentFile = files[index];
                        groupByFolder = !groupByFolder;
                        ApplySort(files, allFiles, sortMode, groupByFolder);
                        index = Array.IndexOf(files, currentFile);
                        lock (preloadLock) { sixelCache.Clear(); }
                        ResetAlt();
                        Redraw();
                    }
                    break;
                case ConsoleKey.D:
                case ConsoleKey.N:
                    {
                        string currentFile = files[index];
                        bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
                        if (key.Key == ConsoleKey.D)
                            sortMode = shift ? SortMode.DateAsc : SortMode.Date;
                        else
                            sortMode = shift ? SortMode.NameDesc : SortMode.Name;
                        ApplySort(files, allFiles, sortMode, groupByFolder);
                        index = Array.IndexOf(files, currentFile);
                        lock (preloadLock) { sixelCache.Clear(); }
                        ResetAlt();
                        Redraw();
                    }
                    break;
                default:
                    if (key.KeyChar == '.')
                    {
                        string currentFile = files[index];
                        sortMode = sortMode == SortMode.Random ? SortMode.Name : SortMode.Random;
                        ApplySort(files, allFiles, sortMode, groupByFolder);
                        index = Array.IndexOf(files, currentFile);
                        lock (preloadLock) { sixelCache.Clear(); }
                        ResetAlt();
                        Redraw();
                    }
                    break;
            }
        }
        else if (autoAdvance && Environment.TickCount64 - lastAdvance >= 3000)
        {
            if (index < files.Length - 1) index++;
            else index = 0;
            ResetAlt();
            lastAdvance = Environment.TickCount64;
            Redraw();
        }
        else
        {
            Thread.Sleep(50);
        }
    }
}
finally
{
    Console.Write("\x1b[?1049l\x1b[?25h");
}

return 0;

static CachedFrame PrepareFrame(string path, int availWidthPx, int availHeightPx, int cellW, int cellH)
{
    using var original = SKBitmap.Decode(path);
    ComputeTargetSize(original.Width, original.Height, availWidthPx, availHeightPx, out int targetW, out int targetH, out _);
    using var resized = ResizeBitmap(original, targetW, targetH);
    Quantize(resized.Bytes, out var palette, out var paletteCount, out var indexed);
    var sixel = SixelEncoder.Encode(indexed, targetW, targetH, palette, paletteCount);
    return new CachedFrame(sixel, targetW, targetH, availWidthPx, availHeightPx, cellW, cellH);
}

static void TrimAndPreload(string[] files, int index, int availWidthPx, int availHeightPx, int cellW, int cellH,
    Dictionary<string, CachedFrame> cache, object preloadLock)
{
    // Determine which paths to keep (prev, current, next)
    var keep = new HashSet<string>();
    keep.Add(files[index]);
    if (index > 0) keep.Add(files[index - 1]);
    if (index < files.Length - 1) keep.Add(files[index + 1]);

    // Evict everything else
    lock (preloadLock)
    {
        var toRemove = new List<string>();
        foreach (var k in cache.Keys)
            if (!keep.Contains(k)) toRemove.Add(k);
        foreach (var k in toRemove)
            cache.Remove(k);
    }

    // Preload next (and prev if not cached) on background thread
    int nextIdx = index < files.Length - 1 ? index + 1 : -1;
    int prevIdx = index > 0 ? index - 1 : -1;

    if (nextIdx >= 0)
    {
        string nextPath = files[nextIdx];
        bool needsPreload;
        lock (preloadLock) { needsPreload = !cache.ContainsKey(nextPath); }
        if (needsPreload)
        {
            int aw = availWidthPx, ah = availHeightPx, cw = cellW, ch = cellH;
            Task.Run(() =>
            {
                try
                {
                    var frame = PrepareFrame(nextPath, aw, ah, cw, ch);
                    lock (preloadLock) { cache[nextPath] = frame; }
                }
                catch { }
            });
        }
    }

    if (prevIdx >= 0)
    {
        string prevPath = files[prevIdx];
        bool needsPreload;
        lock (preloadLock) { needsPreload = !cache.ContainsKey(prevPath); }
        if (needsPreload)
        {
            int aw = availWidthPx, ah = availHeightPx, cw = cellW, ch = cellH;
            Task.Run(() =>
            {
                try
                {
                    var frame = PrepareFrame(prevPath, aw, ah, cw, ch);
                    lock (preloadLock) { cache[prevPath] = frame; }
                }
                catch { }
            });
        }
    }
}

/// <summary>
/// Resize a bitmap to target dimensions, compositing alpha over white.
/// The raw bytes are directly usable as BGRX input for WuColorQuantizer.
/// </summary>
static SKBitmap ResizeBitmap(SKBitmap original, int targetW, int targetH)
{
    var info = new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
    var resized = new SKBitmap(info);
    using var canvas = new SKCanvas(resized);
    canvas.Clear(SKColors.White);
    canvas.DrawBitmap(original, new SKRect(0, 0, targetW, targetH));
    return resized;
}

/// <summary>
/// Quantize BGRX pixel data using Wu's variance-minimizing algorithm.
/// Returns BGRX palette (4 bytes per entry) and indexed pixel data.
/// </summary>
static void Quantize(byte[] bgrx,
    out byte[] palette, out int paletteCount, out byte[] indexed)
{
    var quantizer = new WuColorQuantizer();
    var result = quantizer.Quantize(bgrx, 256);

    palette = result.Palette;
    paletteCount = palette.Length / 4;
    indexed = result.Bytes;
}

static void ComputeTargetSize(int origW, int origH, int conWidthPx, int availHeightPx,
    out int targetW, out int targetH, out float scale)
{
    scale = Math.Min((float)conWidthPx / origW, (float)availHeightPx / origH);
    targetH = Math.Max(6, (int)(origH * scale));
    targetH = targetH / 6 * 6;
    targetW = Math.Max(1, (int)Math.Round(origW * ((double)targetH / origH)));
}

static void ShowImage(string displayPath, int totalFiles, int index, SortMode sortMode, bool groupByFolder, bool autoAdvance,
    Dictionary<string, CachedFrame> cache, object preloadLock, string rootFolder, string? altLabel = null)
{
    string path = displayPath;

    Console.Write("\x1b[2J\x1b[H");
    string order = sortMode switch { SortMode.Random => "RND", SortMode.Date => "DATE\u2191", SortMode.DateAsc => "DATE\u2193", SortMode.NameDesc => "NAME\u2191", _ => "NAME\u2193" };
    string grp = groupByFolder ? " GRP" : "";
    string auto = autoAdvance ? " AUTO" : "";
    string alt = altLabel ?? "";
    var name = Path.GetRelativePath(rootFolder, path);
    Console.WriteLine($" [{index + 1}/{totalFiles}] {name}  [{order}{grp}{auto}{alt}]  (.rnd D/\u21e7D date N/\u21e7N name G grp Space auto \u2190\u2192 nav \u2191\u2193 alt Q quit)");

    GetConsoleSizePixels(out int conWidthPx, out int conHeightPx, out int cellW, out int cellH);

    // Reserve space for border (top/bottom rows, left/right columns in pixel terms)
    int availHeightPx = conHeightPx - 1 * cellH - 2 * cellH; // 1 header line + 2 border rows
    if (availHeightPx < cellH) availHeightPx = cellH;
    availHeightPx = availHeightPx / 6 * 6;
    int availWidthPx = conWidthPx - 2 * cellW; // 2 border columns

    // Check cache
    CachedFrame? frame;
    lock (preloadLock)
    {
        cache.TryGetValue(path, out frame);
        if (frame != null && (frame.AvailWidthPx != availWidthPx || frame.AvailHeightPx != availHeightPx))
            frame = null;
    }

    if (frame == null)
    {
        frame = PrepareFrame(path, availWidthPx, availHeightPx, cellW, cellH);
        lock (preloadLock) { cache[path] = frame; }
    }

    int targetW = frame.TargetW, targetH = frame.TargetH;

    // Image size in character cells
    int imgCols = (targetW + cellW - 1) / cellW;
    int imgRows = (targetH + cellH - 1) / cellH;

    // Box dimensions (image + 1-char border on each side)
    int boxCols = imgCols + 2;
    int boxRow = 2; // row where box top starts (after 1 header line)
    int boxCol = Math.Max(1, (Console.WindowWidth - boxCols) / 2 + 1); // 1-based

    // Draw top border
    Console.Write($"\x1b[{boxRow};{boxCol}H\u250c{new string('\u2500', imgCols)}\u2510");

    // Draw side borders
    for (int r = 0; r < imgRows; r++)
        Console.Write($"\x1b[{boxRow + 1 + r};{boxCol}H\u2502\x1b[{boxRow + 1 + r};{boxCol + imgCols + 1}H\u2502");

    // Draw bottom border
    Console.Write($"\x1b[{boxRow + 1 + imgRows};{boxCol}H\u2514{new string('\u2500', imgCols)}\u2518");

    // Position cursor inside the box for sixel output
    Console.Write($"\x1b[{boxRow + 1};{boxCol + 1}H");

    using var stdout = Console.OpenStandardOutput();
    stdout.Write(frame.Sixel);
    stdout.Flush();

}

/// <summary>
/// The console's pixel geometry, asked of the terminal only when the grid has changed.
/// </summary>
/// <remarks>
/// The query underneath is a round trip with a 200ms deadline, and it used to run once per image
/// shown -- so every keypress cost up to a fifth of a second of spinning on a terminal that does not
/// answer, and swallowed any key pressed while it waited. The row and column count is the resize
/// signal: nothing changes the pixel geometry without changing it too, a font size change included.
/// A failed query is cached like any other answer, so an unresponsive terminal is asked once rather
/// than on every frame.
/// </remarks>
static void GetConsoleSizePixels(out int widthPx, out int heightPx, out int cellW, out int cellH)
{
    int cols = Console.WindowWidth;
    int rows = Console.WindowHeight;

    if (cols != ConsoleSizeCache.Cols || rows != ConsoleSizeCache.Rows)
    {
        QueryConsoleSizePixels(cols, rows,
            out ConsoleSizeCache.WidthPx, out ConsoleSizeCache.HeightPx,
            out ConsoleSizeCache.CellW, out ConsoleSizeCache.CellH);

        ConsoleSizeCache.Cols = cols;
        ConsoleSizeCache.Rows = rows;
    }

    widthPx = ConsoleSizeCache.WidthPx;
    heightPx = ConsoleSizeCache.HeightPx;
    cellW = ConsoleSizeCache.CellW;
    cellH = ConsoleSizeCache.CellH;
}

/// <summary>
/// Asks the terminal for its text area in pixels, and derives the cell size from it.
/// </summary>
static void QueryConsoleSizePixels(int cols, int rows, out int widthPx, out int heightPx, out int cellW, out int cellH)
{
    widthPx = 0;
    heightPx = 0;

    try
    {
        Console.Write("\x1b[14t");
        var sb = new StringBuilder();
        var deadline = Environment.TickCount64 + 200;
        while (Environment.TickCount64 < deadline)
        {
            if (Console.KeyAvailable)
            {
                var c = Console.ReadKey(true).KeyChar;
                sb.Append(c);
                if (c == 't') break;
            }
        }

        var resp = sb.ToString();
        int idx4 = resp.IndexOf('4');
        if (idx4 >= 0 && resp.EndsWith('t'))
        {
            var inner = resp[(idx4 + 1)..^1];
            var parts = inner.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                int.TryParse(parts[0], out heightPx);
                int.TryParse(parts[1], out widthPx);
            }
        }
    }
    catch { }

    if (widthPx > 0 && heightPx > 0)
    {
        cellW = widthPx / cols;
        cellH = heightPx / rows;

        // Round the reported size back down to whole cells.
        //
        // The layout below spends every pixel of heightPx on the image after reserving three rows, so
        // it only fits if heightPx is exactly rows*cellH. But cellH came from flooring that same
        // division, so any remainder is space belonging to no row at all -- and it gets spent anyway,
        // making the image up to a row taller than there is room for. It then runs off the bottom and
        // scrolls the header off the top.
        //
        // A terminal that reports its text area exactly, as xterm does, divides evenly and loses
        // nothing here. One that includes padding or a scrollbar in the figure -- Windows Terminal has
        // a configurable padding, and not every terminal distinguishes text area from window -- is what
        // this guards against.
        widthPx = cols * cellW;
        heightPx = rows * cellH;
    }
    else
    {
        cellW = 8;
        cellH = 16;
        widthPx = cols * cellW;
        heightPx = rows * cellH;
    }
}

/// <summary>
/// High-performance SIXEL encoder.
/// Takes pre-quantized indexed pixel data and palette.
/// Uses SIMD for bitmask building, pooled buffers, direct byte[] output.
/// </summary>
static void RunUpscale(string source, string destination)
{
    var psi = new ProcessStartInfo("upscayl", $"-i \"{source}\" -o \"{destination}\" -n upscayl-lite-4x -s 2")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    var name = Path.GetFileName(source);
    int lines = 0;

    ShowStatus($" Upscaling {name}  [{ProgressBar(0, 10)}]   0%");

    var proc = Process.Start(psi);
    if (proc == null)
        return;

    // Fired on a thread pool thread, which is safe to write from only because the thread that would
    // otherwise be drawing is parked in WaitForExit below.
    void OnData(object sender, DataReceivedEventArgs args)
    {
        if (args.Data == null)
            return;

        int n = Math.Min(Interlocked.Increment(ref lines), 10);
        ShowStatus($" Upscaling {name}  [{ProgressBar(n, 10)}] {n * 100 / 10,3}%");
    }

    proc.OutputDataReceived += OnData;
    proc.ErrorDataReceived += OnData;
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    proc.WaitForExit();
}

/// <summary>
/// Overwrites the caption row, where the header normally sits.
/// </summary>
/// <remarks>
/// Clears to end of line, so a short message does not leave the tail of a longer one behind it.
/// </remarks>
static void ShowStatus(string text) => Console.Write($"\u001b[1;1H{text}\u001b[K");

static string ProgressBar(int current, int total, int width = 20)
{
    int filled = total > 0 ? current * width / total : 0;
    return new string('█', filled) + new string('░', width - filled);
}

static class SixelEncoder
{
    [ThreadStatic] private static byte[]? t_outputBuf;

    public static byte[] Encode(byte[] indexed, int width, int height, byte[] palette, int paletteCount)
    {
        int maxOutput = 64 + paletteCount * 20 + width * ((height + 5) / 6) * 4 + 4096;
        var output = RentOrGrow(ref t_outputBuf, maxOutput);
        int pos = 0;

        // DCS q
        output[pos++] = 0x1B;
        output[pos++] = (byte)'P';
        output[pos++] = (byte)'q';

        // Raster attributes "1;1;W;H
        output[pos++] = (byte)'"';
        output[pos++] = (byte)'1';
        output[pos++] = (byte)';';
        output[pos++] = (byte)'1';
        output[pos++] = (byte)';';
        pos = WriteIntBuf(output, pos, width);
        output[pos++] = (byte)';';
        pos = WriteIntBuf(output, pos, height);

        // Palette: #idx;2;R%;G%;B%
        for (int i = 0; i < paletteCount; i++)
        {
            int r = palette[i * 4 + 2] * 100 / 255;
            int g = palette[i * 4 + 1] * 100 / 255;
            int b = palette[i * 4] * 100 / 255;

            output[pos++] = (byte)'#';
            pos = WriteIntBuf(output, pos, i);
            output[pos++] = (byte)';';
            output[pos++] = (byte)'2';
            output[pos++] = (byte)';';
            pos = WriteIntBuf(output, pos, r);
            output[pos++] = (byte)';';
            pos = WriteIntBuf(output, pos, g);
            output[pos++] = (byte)';';
            pos = WriteIntBuf(output, pos, b);
        }

        // Band encoding
        int bandCount = (height + 5) / 6;
        Span<bool> colorPresent = stackalloc bool[paletteCount];
        var sixelRow = ArrayPool<byte>.Shared.Rent(width);

        try
        {
            for (int band = 0; band < bandCount; band++)
            {
                int yStart = band * 6;
                int bandRows = Math.Min(6, height - yStart);

                colorPresent.Clear();
                for (int row = 0; row < bandRows; row++)
                {
                    int rowOff = (yStart + row) * width;
                    for (int x = 0; x < width; x++)
                        colorPresent[indexed[rowOff + x]] = true;
                }

                int bandWorstCase = paletteCount * (width + 20);
                if (pos + bandWorstCase > output.Length)
                {
                    int newLen = Math.Max(output.Length * 2, pos + bandWorstCase + 4096);
                    var newBuf = new byte[newLen];
                    output.AsSpan(0, pos).CopyTo(newBuf);
                    t_outputBuf = newBuf;
                    output = newBuf;
                }

                bool anyColor = false;
                for (int color = 0; color < paletteCount; color++)
                {
                    if (!colorPresent[color]) continue;

                    BuildSixelRow(indexed, sixelRow, width, yStart, bandRows, (byte)color);

                    if (anyColor)
                        output[pos++] = (byte)'$';

                    output[pos++] = (byte)'#';
                    pos = WriteIntBuf(output, pos, color);
                    pos = WriteRleBuf(output, pos, sixelRow, width);
                    anyColor = true;
                }

                if (band < bandCount - 1)
                    output[pos++] = (byte)'-';
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sixelRow);
        }

        // ST
        output[pos++] = 0x1B;
        output[pos++] = (byte)'\\';

        var result = GC.AllocateUninitializedArray<byte>(pos);
        output.AsSpan(0, pos).CopyTo(result);
        t_outputBuf = output;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BuildSixelRow(byte[] indexed, byte[] sixelRow, int width, int yStart, int bandRows, byte color)
    {
        ref byte rows0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(indexed), yStart * width);
        ref byte outRef = ref MemoryMarshal.GetArrayDataReference(sixelRow);

        if (Vector256.IsHardwareAccelerated && width >= 32)
        {
            var vColor = Vector256.Create(color);
            var v63 = Vector256.Create((byte)63);

            int x = 0;
            for (; x + 32 <= width; x += 32)
            {
                var bits = Vector256<byte>.Zero;

                var eq = Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)x), vColor);
                bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(eq, Vector256.Create((byte)1)));

                if (bandRows > 1)
                {
                    eq = Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)(width + x)), vColor);
                    bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(eq, Vector256.Create((byte)2)));
                }
                if (bandRows > 2)
                {
                    eq = Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)(width * 2 + x)), vColor);
                    bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(eq, Vector256.Create((byte)4)));
                }
                if (bandRows > 3)
                {
                    eq = Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)(width * 3 + x)), vColor);
                    bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(eq, Vector256.Create((byte)8)));
                }
                if (bandRows > 4)
                {
                    eq = Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)(width * 4 + x)), vColor);
                    bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(eq, Vector256.Create((byte)16)));
                }
                if (bandRows > 5)
                {
                    eq = Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)(width * 5 + x)), vColor);
                    bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(eq, Vector256.Create((byte)32)));
                }

                Vector256.Add(bits, v63).StoreUnsafe(ref outRef, (nuint)x);
            }

            for (; x < width; x++)
                Unsafe.Add(ref outRef, x) = BuildSixelScalar(ref rows0, x, width, bandRows, color);
        }
        else if (Vector128.IsHardwareAccelerated && width >= 16)
        {
            var vColor = Vector128.Create(color);
            var v63 = Vector128.Create((byte)63);

            int x = 0;
            for (; x + 16 <= width; x += 16)
            {
                var bits = Vector128<byte>.Zero;

                var eq = Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)x), vColor);
                bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(eq, Vector128.Create((byte)1)));

                if (bandRows > 1)
                {
                    eq = Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)(width + x)), vColor);
                    bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(eq, Vector128.Create((byte)2)));
                }
                if (bandRows > 2)
                {
                    eq = Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)(width * 2 + x)), vColor);
                    bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(eq, Vector128.Create((byte)4)));
                }
                if (bandRows > 3)
                {
                    eq = Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)(width * 3 + x)), vColor);
                    bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(eq, Vector128.Create((byte)8)));
                }
                if (bandRows > 4)
                {
                    eq = Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)(width * 4 + x)), vColor);
                    bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(eq, Vector128.Create((byte)16)));
                }
                if (bandRows > 5)
                {
                    eq = Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)(width * 5 + x)), vColor);
                    bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(eq, Vector128.Create((byte)32)));
                }

                Vector128.Add(bits, v63).StoreUnsafe(ref outRef, (nuint)x);
            }

            for (; x < width; x++)
                Unsafe.Add(ref outRef, x) = BuildSixelScalar(ref rows0, x, width, bandRows, color);
        }
        else
        {
            for (int x = 0; x < width; x++)
                Unsafe.Add(ref outRef, x) = BuildSixelScalar(ref rows0, x, width, bandRows, color);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte BuildSixelScalar(ref byte rows0, int x, int width, int bandRows, byte color)
    {
        int bits = 0;
        if (Unsafe.Add(ref rows0, x) == color) bits |= 1;
        if (bandRows > 1 && Unsafe.Add(ref rows0, width + x) == color) bits |= 2;
        if (bandRows > 2 && Unsafe.Add(ref rows0, width * 2 + x) == color) bits |= 4;
        if (bandRows > 3 && Unsafe.Add(ref rows0, width * 3 + x) == color) bits |= 8;
        if (bandRows > 4 && Unsafe.Add(ref rows0, width * 4 + x) == color) bits |= 16;
        if (bandRows > 5 && Unsafe.Add(ref rows0, width * 5 + x) == color) bits |= 32;
        return (byte)(bits + 63);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteIntBuf(byte[] buf, int pos, int value)
    {
        if (value < 10)
        {
            buf[pos] = (byte)('0' + value);
            return pos + 1;
        }
        if (value < 100)
        {
            buf[pos] = (byte)('0' + value / 10);
            buf[pos + 1] = (byte)('0' + value % 10);
            return pos + 2;
        }
        int tmp = value;
        int digits = 0;
        while (tmp > 0) { digits++; tmp /= 10; }
        pos += digits;
        int p = pos;
        while (value > 0)
        {
            buf[--p] = (byte)('0' + value % 10);
            value /= 10;
        }
        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteRleBuf(byte[] output, int pos, byte[] data, int length)
    {
        int i = 0;
        while (i < length)
        {
            byte ch = data[i];
            int run = 1;
            while (i + run < length && data[i + run] == ch)
                run++;

            if (run >= 4)
            {
                output[pos++] = (byte)'!';
                pos = WriteIntBuf(output, pos, run);
                output[pos++] = ch;
            }
            else if (run == 3)
            {
                output[pos++] = ch;
                output[pos++] = ch;
                output[pos++] = ch;
            }
            else if (run == 2)
            {
                output[pos++] = ch;
                output[pos++] = ch;
            }
            else
            {
                output[pos++] = ch;
            }
            i += run;
        }
        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T[] RentOrGrow<T>(ref T[]? buf, int minSize)
    {
        if (buf == null || buf.Length < minSize)
            buf = GC.AllocateUninitializedArray<T>(Math.Max(minSize, 4096));
        return buf;
    }
}

/// <summary>
/// Runs upscayl over one image, drawing a progress bar over the caption row while it works.
/// </summary>
/// <remarks>
/// Blocking on purpose. The key loop is in ReadKey, so there is nothing to return control to, and
/// upscayl's own output is the only progress signal there is -- it does not report a percentage, so
/// the bar counts lines and treats ten of them as a run. The bar moving is what it is for; the
/// number under it is a guess and stops at 100 either way.
/// </remarks>

record CachedFrame(byte[] Sixel, int TargetW, int TargetH, int AvailWidthPx, int AvailHeightPx, int CellW, int CellH);

enum SortMode { Random, Name, NameDesc, Date, DateAsc }

/// <summary>
/// The last answer the terminal gave about its pixel geometry, and the grid it was given for.
/// </summary>
/// <remarks>
/// A type rather than locals because the program is written as top-level statements, where the
/// helpers are static local functions and so cannot capture anything.
/// <para><see cref="Cols"/> starts at -1 so the first call can never match and always asks.</para>
/// </remarks>
static class ConsoleSizeCache
{
    public static int Cols = -1;
    public static int Rows = -1;
    public static int WidthPx;
    public static int HeightPx;
    public static int CellW;
    public static int CellH;
}
