using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using JeremyAnsel.ColorQuant;
using SkiaSharp;
using Microsoft.Win32.SafeHandles;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: six <folder>");
    Console.Error.WriteLine("       six --probe    ask the terminal what it supports and print the answers");
    return 1;
}

if (args[0] is "--probe" or "-p")
{
    ProbeTerminal();
    return 0;
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

// Ask before drawing anything: which protocol is in play decides how a frame is encoded, and
// frames start being encoded on background threads with the very first image.
GraphicsMode.Kitty = DetectKittyGraphics();

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
        availH = GraphicsMode.AlignHeight(availH);
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
    // Hand the pictures back before the screen they were drawn on goes away.
    if (GraphicsMode.Kitty)
        Console.Write(KittyEncoder.DeleteAll);

    Console.Write("\x1b[?1049l\x1b[?25h");
}

return 0;

static CachedFrame PrepareFrame(string path, int availWidthPx, int availHeightPx, int cellW, int cellH)
{
    // Only worth asking on a terminal that could play one.
    if (GraphicsMode.Kitty
        && TryPrepareAnimation(path, availWidthPx, availHeightPx, cellW, cellH, out var animation))
        return animation;

    using var original = SKBitmap.Decode(path);
    ComputeTargetSize(original.Width, original.Height, availWidthPx, availHeightPx, out int targetW, out int targetH, out _);
    using var resized = ResizeBitmap(original, targetW, targetH);

    byte[] data;
    if (GraphicsMode.Kitty)
    {
        // Kitty takes a picture whole and in full colour, so there is no palette to squeeze it
        // into -- the 256 colour quantization the sixel path needs is skipped entirely.
        data = KittyEncoder.Encode(resized.Bytes, targetW, targetH);
    }
    else
    {
        Quantize(resized.Bytes, out var palette, out var paletteCount, out var indexed);
        data = SixelEncoder.Encode(indexed, targetW, targetH, palette, paletteCount);
    }

    return new CachedFrame(data, targetW, targetH, availWidthPx, availHeightPx, cellW, cellH);
}

/// <summary>
/// Encodes a file that holds more than one frame as a Kitty animation.
/// </summary>
/// <remarks>
/// <para>False for anything with a single frame, which falls through to the still path, and for a
/// file whose frames will not decode. A terminal without animation is never asked.</para>
/// <para>Frames go out at the size they were drawn at rather than the size they are shown at, and
/// the terminal scales the placement to the box. Enlarging every frame to fill the screen first
/// would cost a resize apiece and multiply the bytes to deflate by ten or more, and it has to be
/// ready before the arrow key that asked for it is let go. A recording larger than the screen is
/// still scaled down here, where it saves work rather than making it.</para>
/// </remarks>
static bool TryPrepareAnimation(string path, int availWidthPx, int availHeightPx, int cellW, int cellH,
    out CachedFrame prepared)
{
    // A wall against a pathological file, not a considered limit. Both are far above any real
    // animation, and stopping early costs the tail of one rather than the whole picture.
    const int MaxFrames = 400;
    const int MaxBytes = 24 * 1024 * 1024;

    prepared = null!;

    using var codec = SKCodec.Create(path);
    if (codec is null || codec.FrameCount < 2)
        return false;

    int nativeW = codec.Info.Width;
    int nativeH = codec.Info.Height;
    if (nativeW <= 0 || nativeH <= 0)
        return false;

    ComputeTargetSize(nativeW, nativeH, availWidthPx, availHeightPx, out int showW, out int showH, out _);
    int sendW = Math.Min(nativeW, showW);
    int sendH = Math.Max(1, (int)Math.Round(nativeH * ((double)sendW / nativeW)));
    int cols = Math.Max(1, showW / cellW);
    int rows = Math.Max(1, showH / cellH);

    var frames = codec.FrameInfo;
    var builder = new KittyEncoder.AnimationBuilder(sendW, sendH, cols, rows);

    var decodeInfo = new SKImageInfo(nativeW, nativeH, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var decoded = new SKBitmap(decodeInfo);
    using var flattened = new SKBitmap(new SKImageInfo(sendW, sendH, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(flattened);
    var rgb = new byte[sendW * sendH * 3];

    int count = Math.Min(frames.Length, MaxFrames);
    for (int i = 0; i < count; i++)
    {
        // Handing the decoder the frame before saves it decoding the chain again to get there, but
        // only when that is the frame this one is built on -- a GIF may reach further back.
        var options = i > 0 && frames[i].RequiredFrame == i - 1
            ? new SKCodecOptions(i, i - 1)
            : new SKCodecOptions(i);

        if (codec.GetPixels(decodeInfo, decoded.GetPixels(), options) != SKCodecResult.Success)
            break;

        // Onto white and down to size in one draw, so a frame matches what the still path would have
        // made of it. The decoded bitmap keeps its transparency, because that is what the decoder
        // wants back when it composites the next frame onto it.
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(decoded, new SKRect(0, 0, sendW, sendH));
        PackRgb(flattened.Bytes, rgb);

        // A frame asking for no delay, or for a delay too short to draw, means as fast as it can be
        // managed. Everyone settled on a tenth of a second for that a long time ago.
        int gap = frames[i].Duration;
        builder.Add(rgb, gap <= 10 ? 100 : gap);

        if (builder.Length >= MaxBytes)
            break;
    }

    if (builder.FrameCount < 2)
        return false;

    // The box drawn around the picture is measured in cells, so the size reported back is the cell
    // box rather than the pixels inside it.
    prepared = new CachedFrame(builder.Build(), cols * cellW, rows * cellH,
        availWidthPx, availHeightPx, cellW, cellH);
    return true;
}

/// <summary>Packs premultiplied BGRA down to the RGB the protocol calls f=24.</summary>
static void PackRgb(byte[] bgra, byte[] rgb)
{
    for (int src = 0, dst = 0; dst < rgb.Length; src += 4, dst += 3)
    {
        rgb[dst] = bgra[src + 2];
        rgb[dst + 1] = bgra[src + 1];
        rgb[dst + 2] = bgra[src];
    }
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
    targetH = Math.Max(GraphicsMode.HeightGrain, (int)(origH * scale));
    targetH = GraphicsMode.AlignHeight(targetH);
    targetW = Math.Max(1, (int)Math.Round(origW * ((double)targetH / origH)));
}

static void ShowImage(string displayPath, int totalFiles, int index, SortMode sortMode, bool groupByFolder, bool autoAdvance,
    Dictionary<string, CachedFrame> cache, object preloadLock, string rootFolder, string? altLabel = null)
{
    string path = displayPath;

    Console.Write("\x1b[2J\x1b[H");
    if (GraphicsMode.Kitty)
        Console.Write(KittyEncoder.DeleteAll);

    string order = sortMode switch { SortMode.Random => "RND", SortMode.Date => "DATE\u2191", SortMode.DateAsc => "DATE\u2193", SortMode.NameDesc => "NAME\u2191", _ => "NAME\u2193" };
    string grp = groupByFolder ? " GRP" : "";
    string auto = autoAdvance ? " AUTO" : "";
    string alt = altLabel ?? "";
    string gfx = GraphicsMode.Kitty ? " KITTY" : " SIXEL";
    var name = Path.GetRelativePath(rootFolder, path);
    Console.WriteLine($" [{index + 1}/{totalFiles}] {name}  [{order}{grp}{auto}{alt}{gfx}]  (.rnd D/\u21e7D date N/\u21e7N name G grp Space auto \u2190\u2192 nav \u2191\u2193 alt Q quit)");

    GetConsoleSizePixels(out int conWidthPx, out int conHeightPx, out int cellW, out int cellH);

    // Reserve space for border (top/bottom rows, left/right columns in pixel terms)
    int availHeightPx = conHeightPx - 1 * cellH - 2 * cellH; // 1 header line + 2 border rows
    if (availHeightPx < cellH) availHeightPx = cellH;
    availHeightPx = GraphicsMode.AlignHeight(availHeightPx);
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
    stdout.Write(frame.Data);
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

    var resp = QueryTerminal("\x1b[14t", 't', 200);
    int idx4 = resp.IndexOf('4');
    int end = resp.LastIndexOf('t');
    if (idx4 >= 0 && end > idx4)
    {
        var inner = resp[(idx4 + 1)..end];
        var parts = inner.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            int.TryParse(parts[0], out heightPx);
            int.TryParse(parts[1], out widthPx);
        }
    }

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

/// <summary>
/// Asks the terminal whether it speaks the Kitty graphics protocol.
/// </summary>
/// <remarks>
/// <para>This is the protocol's own recipe for the question: transmit a one pixel image and ask
/// about it rather than display it, then chase that with a primary device attributes request. A
/// terminal that implements graphics answers the first; every terminal answers the second.</para>
/// <para>The attributes request is what makes silence conclusive. Without something guaranteed to
/// come back behind it there is no telling a terminal that means no from one that has not got round
/// to answering yet, and the only recourse is to wait out the whole timeout on every start.</para>
/// <para>The picture is never displayed and the id is never used again, so the question costs the
/// terminal nothing but a parse. SIX_GRAPHICS forces the answer either way, for a terminal that
/// gets it wrong.</para>
/// </remarks>
static bool DetectKittyGraphics()
{
    var forced = Environment.GetEnvironmentVariable("SIX_GRAPHICS");
    if (string.Equals(forced, "kitty", StringComparison.OrdinalIgnoreCase))
        return true;
    if (string.Equals(forced, "sixel", StringComparison.OrdinalIgnoreCase))
        return false;

    // The attributes reply ends in 'c', and is answered after the graphics query, so it is the
    // terminal having said everything it has to say.
    return SpeaksKittyGraphics(QueryTerminal(KittyEncoder.SupportQuery, 'c', 500));
}

/// <summary>
/// Reads the answer to <see cref="KittyEncoder.SupportQuery"/>.
/// </summary>
/// <remarks>
/// The reply to look for is <c>ESC _G i=31;OK ESC \</c>, but only the OK is looked for. The reply
/// arrives here one keystroke at a time through the console, which is not a faithful pipe for the
/// escape and the APC introducer in front of it -- a Windows console hands those to a program as
/// key events that need not survive the trip. The two letters do survive, and no device attributes
/// reply contains them, so they are enough to tell one answer from the other.
/// </remarks>
static bool SpeaksKittyGraphics(string response) => response.Contains(";OK", StringComparison.Ordinal);

/// <summary>
/// Sends a request to the terminal and collects the reply, as far as a terminating character.
/// </summary>
/// <remarks>
/// <para>Every one of these is a round trip against a deadline, because there is no telling in
/// advance whether the terminal on the other end implements the request at all. One that does not
/// says nothing, and waiting is the only way to find that out.</para>
/// <para>Whatever is buffered behind the terminator is taken as well. Replies come back in the
/// order the requests were made, so reading only as far as the first terminator would leave a
/// second reply sitting in the input for the key loop to read as keystrokes.</para>
/// </remarks>
static string QueryTerminal(string request, char terminator, int timeoutMs)
{
    var sb = new StringBuilder();

    try
    {
        using var passthrough = VirtualTerminalInput.Enable();
        Console.Write(request);

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(1);
                continue;
            }

            char c = Console.ReadKey(true).KeyChar;
            sb.Append(c);

            if (c == terminator)
            {
                while (Console.KeyAvailable)
                    sb.Append(Console.ReadKey(true).KeyChar);
                break;
            }
        }
    }
    catch { }

    return sb.ToString();
}

/// <summary>
/// Prints what the terminal says to the questions asked of it on the way in.
/// </summary>
/// <remarks>
/// For a terminal that draws nothing, or draws in the wrong protocol. Both answers are guesses
/// about a terminal made from a few bytes it sent back, and this is the only way to see the bytes.
/// </remarks>
static void ProbeTerminal()
{
    Console.WriteLine($"Console input passthrough: {VirtualTerminalInput.Describe()}");
    Console.WriteLine();

    var kitty = QueryTerminal(KittyEncoder.SupportQuery, 'c', 1000);
    Console.WriteLine("Kitty graphics");
    Console.WriteLine($"  sent     {Describe(KittyEncoder.SupportQuery)}");
    Console.WriteLine($"  received {Describe(kitty)}");
    Console.WriteLine(SpeaksKittyGraphics(kitty)
        ? "  verdict  supported -- pictures drawn in 24-bit colour"
        : "  verdict  not supported -- pictures drawn as sixel, 256 colours");
    Console.WriteLine();

    var size = QueryTerminal("\x1b[14t", 't', 1000);
    Console.WriteLine("Text area size");
    Console.WriteLine($"  sent     {Describe("\x1b[14t")}");
    Console.WriteLine($"  received {Describe(size)}");
    GetConsoleSizePixels(out int widthPx, out int heightPx, out int cellW, out int cellH);
    Console.WriteLine($"  verdict  {Console.WindowWidth}x{Console.WindowHeight} cells, "
        + $"{widthPx}x{heightPx} px, cell {cellW}x{cellH} px");
}

/// <summary>
/// Renders an escape sequence so it can be read on a screen rather than obeyed by one.
/// </summary>
static string Describe(string sequence)
{
    if (sequence.Length == 0)
        return "(nothing)";

    var sb = new StringBuilder();
    foreach (char c in sequence)
    {
        sb.Append(c switch
        {
            '\x1b' => "<ESC>",
            < ' ' => $"<{(int)c:X2}>",
            _ => c.ToString(),
        });
    }

    return sb.ToString();
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

/// <summary>
/// Wraps a picture in the Kitty graphics protocol's escape sequences.
/// </summary>
static class KittyEncoder
{
    /// <summary>The most base64 the protocol allows one escape sequence to carry.</summary>
    private const int MaxChunk = 4096;

    /// <summary>
    /// The id every picture is sent under.
    /// </summary>
    /// <remarks>
    /// One id is enough because only one picture is ever on the screen, and the one before it has
    /// been deleted by the time the next arrives. An animation needs the id for a second reason: its
    /// frames travel in escape codes of their own and each has to say which picture it belongs to.
    /// </remarks>
    private const int ImageId = 1;

    /// <summary>
    /// Asks whether the terminal speaks the protocol, and forces it to say either way.
    /// </summary>
    /// <remarks>
    /// <para>The protocol's own recipe: transmit a one pixel image and query it rather than display
    /// it. i=31 is an arbitrary id, quoted back in the reply so the answer can be told apart from
    /// any other escape sequence arriving at the same moment, and AAAA is one black RGB pixel. The
    /// picture is never shown and the id is never used again, so the question costs a parse.</para>
    /// <para>The device attributes request on the end is what makes silence conclusive. Without
    /// something guaranteed to come back behind it there is no telling a terminal that means no from
    /// one that has not got round to answering, and the only recourse is to wait out the whole
    /// timeout on every start.</para>
    /// </remarks>
    public const string SupportQuery = "\x1b_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\x1b\\\x1b[c";

    /// <summary>
    /// Removes every picture on the screen, and the pixels behind them.
    /// </summary>
    /// <remarks>
    /// Erasing the screen does not erase graphics -- pictures are not text, and a clear does not
    /// reach them. Every image here is transmitted afresh when it is shown, so there is nothing
    /// behind the placements worth keeping either.
    /// </remarks>
    public const string DeleteAll = "\x1b_Ga=d,d=A,q=2\x1b\\";

    /// <summary>
    /// Builds the sequence that shows a still picture at the cursor, from premultiplied BGRA pixels.
    /// </summary>
    /// <remarks>
    /// <para>Raw pixels compressed with zlib rather than a PNG. Both are in the protocol, and on a
    /// photograph a PNG is about a quarter smaller -- but it costs twice the CPU to produce, and five
    /// to nine times as much on the flatter pictures, where it is not even the smaller of the two.
    /// Encoding is what the viewer spends its time on when an arrow key is held down, so that is the
    /// side to buy.</para>
    /// <para>Alpha is dropped on the way. The picture was composited onto white when it was resized,
    /// so every pixel is opaque and a fourth byte would be a quarter of the payload saying so.</para>
    /// </remarks>
    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        // a=T take it and show it here, f=24 three bytes a pixel, t=d the bytes are inline, o=z they
        // are zlib compressed, C=1 leave the cursor alone. The raw formats carry no dimensions of
        // their own, hence s and v. q=2 matters most: the key loop reads stdin raw, and an
        // acknowledgement would arrive there as a keystroke and eat one.
        var sb = new StringBuilder();
        Append(sb, $"a=T,i={ImageId},f=24,t=d,o=z,s={width},v={height},C=1,q=2",
            Compress(bgra, width, height));

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds one Kitty animation: a root picture, a frame for every change after it, and the
    /// command that sets the terminal playing it.
    /// </summary>
    /// <remarks>
    /// <para>Playback is the terminal's, not this program's. Once the frames are across, the terminal
    /// runs them on its own clock and nothing further is sent -- which is the only reason animation
    /// fits here at all, because the key loop spends its life parked in ReadKey and cannot be drawing
    /// at the same time.</para>
    /// <para>Frames are numbered from one and the root picture is frame one. Each frame after it is
    /// sent as the rectangle that changed, composited onto a copy of the frame before it. That is the
    /// shape a GIF was already in before it was decoded, so this mostly puts back what decoding took
    /// apart.</para>
    /// </remarks>
    public sealed class AnimationBuilder
    {
        private readonly StringBuilder _sb = new();
        private readonly int _width;
        private readonly int _height;
        private readonly int _cols;
        private readonly int _rows;
        private byte[]? _previous;

        public AnimationBuilder(int width, int height, int cols, int rows)
        {
            _width = width;
            _height = height;
            _cols = cols;
            _rows = rows;
        }

        /// <summary>How much has been built so far, for a caller keeping to a budget.</summary>
        public int Length => _sb.Length;

        public int FrameCount { get; private set; }

        /// <summary>
        /// Adds one fully composited frame, packed as RGB. The buffer may be reused afterwards.
        /// </summary>
        public void Add(byte[] rgb, int gapMs)
        {
            if (FrameCount == 0)
            {
                // Both c and r are given, so the terminal letterboxes the picture into that many cells
                // rather than stretching it, and it keeps its shape whatever the cell size turns out
                // to be. Sending it at the size it was decoded at and letting the terminal do the
                // scaling is the whole economy of this path.
                Append(_sb, $"a=T,i={ImageId},f=24,t=d,o=z,s={_width},v={_height}"
                    + $",c={_cols},r={_rows},C=1,q=2", Deflate(rgb, _width, 0, 0, _width, _height));

                // The root frame's gap cannot ride along with it -- it went as a picture, not as a
                // frame -- so it is set afterwards, naming frame one.
                if (gapMs > 0)
                    Append(_sb, $"a=a,i={ImageId},r=1,z={gapMs},q=2", Array.Empty<byte>());
            }
            else
            {
                if (!DirtyRect(_previous!, rgb, _width, _height, out int x, out int y, out int w, out int h))
                {
                    // Nothing moved. The frame still has to exist, or the pause it stands for would
                    // vanish and the animation would run through it early, so one pixel is resent.
                    x = y = 0;
                    w = h = 1;
                }

                // a=f adds a frame, c names the frame underneath it, x and y place the changed
                // rectangle on that frame, X=1 overwrites rather than blends -- there is no alpha in
                // f=24 to blend with -- and z is how long the frame is held.
                Append(_sb, $"a=f,i={ImageId},f=24,t=d,o=z,s={w},v={h},x={x},y={y}"
                    + $",c={FrameCount},z={gapMs},X=1,q=2", Deflate(rgb, _width, x, y, w, h));
            }

            _previous ??= new byte[rgb.Length];
            rgb.AsSpan().CopyTo(_previous);
            FrameCount++;
        }

        /// <summary>Closes the animation with the command that starts it.</summary>
        /// <remarks>s=3 runs it and loops back at the end; v=1 means forever.</remarks>
        public byte[] Build()
        {
            Append(_sb, $"a=a,i={ImageId},s=3,v=1,q=2", Array.Empty<byte>());
            return Encoding.ASCII.GetBytes(_sb.ToString());
        }
    }

    /// <summary>
    /// Writes one command, splitting its payload across as many escape codes as the protocol needs.
    /// </summary>
    private static void Append(StringBuilder sb, string controlKeys, byte[] payload)
    {
        if (payload.Length == 0)
        {
            sb.Append("\x1b_G").Append(controlKeys).Append("\x1b\\");
            return;
        }

        var b64 = Convert.ToBase64String(payload);
        for (int offset = 0; offset < b64.Length; offset += MaxChunk)
        {
            int length = Math.Min(MaxChunk, b64.Length - offset);
            bool last = offset + length == b64.Length;

            sb.Append("\x1b_G");
            if (offset == 0)
                sb.Append(controlKeys).Append(',');

            // Continuation chunks carry m and nothing else, which is what the protocol asks for.
            sb.Append("m=").Append(last ? '0' : '1').Append(';');
            sb.Append(b64, offset, length);
            sb.Append("\x1b\\");
        }
    }

    /// <summary>
    /// Packs BGRA down to the RGB the protocol calls f=24, and deflates it.
    /// </summary>
    /// <remarks>
    /// A row at a time, so a picture is never held twice over -- the packed copy of a full screen
    /// photograph is several megabytes, and it would be built only to be thrown away a moment later.
    /// </remarks>
    private static byte[] Compress(byte[] bgra, int width, int height)
    {
        var compressed = new MemoryStream(width * height);
        var row = new byte[width * 3];

        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                int src = y * width * 4;
                for (int dst = 0; dst < row.Length; dst += 3, src += 4)
                {
                    row[dst] = bgra[src + 2];
                    row[dst + 1] = bgra[src + 1];
                    row[dst + 2] = bgra[src];
                }

                zlib.Write(row);
            }
        }

        return compressed.ToArray();
    }

    /// <summary>Deflates one rectangle of an already packed RGB picture.</summary>
    private static byte[] Deflate(byte[] rgb, int imageWidth, int x, int y, int width, int height)
    {
        var compressed = new MemoryStream(width * height);
        int stride = imageWidth * 3;

        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int row = 0; row < height; row++)
                zlib.Write(rgb, (y + row) * stride + x * 3, width * 3);
        }

        return compressed.ToArray();
    }

    /// <summary>
    /// Finds the smallest rectangle holding every pixel that changed between two frames.
    /// </summary>
    /// <remarks>
    /// False when the two are identical, which happens more than it sounds: a GIF that pauses does it
    /// by repeating a frame.
    /// </remarks>
    private static bool DirtyRect(byte[] previous, byte[] current, int width, int height,
        out int x, out int y, out int rectWidth, out int rectHeight)
    {
        int stride = width * 3;

        int top = 0;
        while (top < height && RowsMatch(top))
            top++;

        if (top == height)
        {
            x = y = rectWidth = rectHeight = 0;
            return false;
        }

        int bottom = height - 1;
        while (bottom > top && RowsMatch(bottom))
            bottom--;

        int left = width;
        int right = -1;
        for (int row = top; row <= bottom; row++)
        {
            int start = row * stride;

            for (int column = 0; column < left; column++)
            {
                if (!PixelsMatch(start + column * 3))
                {
                    left = column;
                    break;
                }
            }

            for (int column = width - 1; column > right; column--)
            {
                if (!PixelsMatch(start + column * 3))
                {
                    right = column;
                    break;
                }
            }
        }

        x = left;
        y = top;
        rectWidth = right - left + 1;
        rectHeight = bottom - top + 1;
        return true;

        bool RowsMatch(int row) => previous.AsSpan(row * stride, stride)
            .SequenceEqual(current.AsSpan(row * stride, stride));

        bool PixelsMatch(int offset) => previous[offset] == current[offset]
            && previous[offset + 1] == current[offset + 1]
            && previous[offset + 2] == current[offset + 2];
    }
}

/// <summary>
/// Turns on the Windows console's pass-through of terminal replies, for as long as one is expected.
/// </summary>
/// <remarks>
/// <para>Without it the console reads the escape sequences arriving from the terminal and hands the
/// program key events it made out of them. A CSI reply comes through that as its own characters, so
/// the text area query has always worked. An APC reply -- which is what the Kitty graphics protocol
/// answers with -- does not: it is swallowed as far as the escape that ends it, and all the program
/// is given is the lone backslash left over, from which it concludes the terminal said nothing and
/// falls back to sixel on a terminal that draws in full colour.</para>
/// <para>Held only for the round trip. The key loop wants the console's reading of an arrow key,
/// not the three characters the terminal actually sent.</para>
/// </remarks>
static class VirtualTerminalInput
{
    private const int StdInputHandle = -10;
    private const uint EnableVirtualTerminalInput = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint handle, uint mode);

    /// <summary>
    /// Sets the mode, and puts back whatever was there before when disposed.
    /// </summary>
    /// <remarks>
    /// The default value restores nothing, which is what every case that could not set the mode --
    /// another operating system, output redirected to a file, a console that refused -- returns.
    /// </remarks>
    public readonly struct Scope : IDisposable
    {
        private readonly nint _handle;
        private readonly uint _mode;
        private readonly bool _restore;

        internal Scope(nint handle, uint mode)
        {
            _handle = handle;
            _mode = mode;
            _restore = true;
        }

        public void Dispose()
        {
            if (_restore)
                SetConsoleMode(_handle, _mode);
        }
    }

    public static Scope Enable()
    {
        if (!OperatingSystem.IsWindows())
            return default;

        try
        {
            var handle = GetStdHandle(StdInputHandle);
            if (handle == 0 || handle == -1)
                return default;
            if (!GetConsoleMode(handle, out uint mode))
                return default;

            // Already on is not this code's doing, so it is not this code's to turn off again.
            if ((mode & EnableVirtualTerminalInput) != 0)
                return default;

            return SetConsoleMode(handle, mode | EnableVirtualTerminalInput)
                ? new Scope(handle, mode)
                : default;
        }
        catch
        {
            return default;
        }
    }

    public static string Describe()
    {
        if (!OperatingSystem.IsWindows())
            return "not needed on this platform";

        try
        {
            var handle = GetStdHandle(StdInputHandle);
            if (handle == 0 || handle == -1 || !GetConsoleMode(handle, out uint mode))
                return "unavailable -- input is not a console";

            return (mode & EnableVirtualTerminalInput) != 0
                ? "already on"
                : "off, turned on for the length of each query below";
        }
        catch
        {
            return "unavailable";
        }
    }
}

/// <summary>
/// Which graphics protocol the terminal turned out to speak.
/// </summary>
/// <remarks>
/// A type rather than locals for the same reason <see cref="ConsoleSizeCache"/> is one: the drawing
/// helpers are static local functions and so cannot capture anything.
/// </remarks>
static class GraphicsMode
{
    public static bool Kitty;

    /// <summary>
    /// The pixel rows a picture's height has to be a whole number of.
    /// </summary>
    /// <remarks>
    /// Sixel draws in bands of six rows and rounds a partial band up, so a height that is not a
    /// multiple of six overruns the space reserved for it and pushes the header off the top. Kitty
    /// places a picture at the size it arrives and has no such grain.
    /// </remarks>
    public static int HeightGrain => Kitty ? 1 : 6;

    public static int AlignHeight(int heightPx) => heightPx / HeightGrain * HeightGrain;
}

record CachedFrame(byte[] Data, int TargetW, int TargetH, int AvailWidthPx, int AvailHeightPx, int CellW, int CellH);

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
