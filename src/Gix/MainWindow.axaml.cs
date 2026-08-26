using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace GixelViewer;

public partial class MainWindow : Window
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tiff", ".tif" };

    string _folder = "";
    string[] _allFiles = Array.Empty<string>();
    string[] _files = Array.Empty<string>();
    int _index;
    SortMode _sortMode = SortMode.Random;
    bool _groupByFolder;
    bool _autoAdvance;
    DispatcherTimer? _timer;

    // Alt cycling
    int _altIndex = -1;
    string? _altBasePath;
    string[] _altFiles = Array.Empty<string>();

    // Delete confirmation
    bool _pendingDelete;

    // Upscale guard
    bool _upscaling;

    // Search mode
    bool _searchMode;
    string _searchQuery = "";

    // Bitmap cache: path -> Bitmap (keeps current, prev, next)
    readonly Dictionary<string, Bitmap> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
    readonly object _cacheLock = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    public async void PickFolderOnLoad()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select image folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            Initialize(path);
        else
            Close();
    }

    public void Initialize(string folder)
    {
        _folder = Path.GetFullPath(folder);
        if (!Directory.Exists(_folder))
        {
            Header.Text = $"Directory not found: {_folder}";
            return;
        }

        _allFiles = Directory.EnumerateFiles(_folder, "*.*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .ToArray();
        Array.Sort(_allFiles, StringComparer.OrdinalIgnoreCase);

        _files = (string[])_allFiles.Clone();
        ApplySort(_files, _allFiles, SortMode.Random, false);

        if (_files.Length == 0)
        {
            Header.Text = "No image files found in the specified folder.";
            return;
        }

        _index = 0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) =>
        {
            if (!_autoAdvance) return;
            _index = _index < _files.Length - 1 ? _index + 1 : 0;
            ResetAlt();
            Redraw();
        };

        Redraw();
    }

    string DisplayPath() =>
        (_altIndex >= 0 && _altIndex < _altFiles.Length && _altBasePath == _files[_index]) ? _altFiles[_altIndex] : _files[_index];

    void ResetAlt()
    {
        _altIndex = -1;
        _altBasePath = null;
        _altFiles = Array.Empty<string>();
    }

    void Redraw()
    {
        if (_files.Length == 0)
        {
            if (_searchMode)
            {
                Header.Text = $" /{_searchQuery}\u2582  No matches";
                ImageView.Source = null;
            }
            return;
        }

        var displayPath = DisplayPath();

        // Update header
        string order = _sortMode switch
        {
            SortMode.Random => "RND",
            SortMode.Date => "DATE\u2191",
            SortMode.DateAsc => "DATE\u2193",
            SortMode.NameDesc => "NAME\u2191",
            SortMode.Size => "SIZE\u2191",
            SortMode.SizeAsc => "SIZE\u2193",
            _ => "NAME\u2193"
        };
        string grp = _groupByFolder ? " GRP" : "";
        string auto = _autoAdvance ? " AUTO" : "";
        string alt = _altIndex >= 0 ? $" ALT {_altIndex + 1}/{_altFiles.Length}" : "";
        var name = Path.GetRelativePath(_folder, displayPath);
        Title = $"GixelViewer - {name}";

        // Load from cache or decode
        var bitmap = GetOrLoadBitmap(displayPath);
        ImageView.Source = bitmap;

        string dims = bitmap != null ? $"[{bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}]" : "";
        if (_searchMode)
            Header.Text = $" /{_searchQuery}\u2582  [{_index + 1}/{_files.Length}] {name}  {dims}";
        else
            Header.Text = $" [{_index + 1}/{_files.Length}] {name}  [{order}{grp}{auto}{alt}]  (.rnd D/\u21e7D date N/\u21e7N name S/\u21e7S size G grp Space auto /search \u2190\u2192 nav \u2191\u2193 alt Q quit)  {dims}";

        // Preload prev/next and evict stale entries
        TrimAndPreload(_files, _index);
    }

    Bitmap? GetOrLoadBitmap(string path)
    {
        lock (_cacheLock)
        {
            if (_bitmapCache.TryGetValue(path, out var cached))
                return cached;
        }
        try
        {
            var bmp = new Bitmap(path);
            lock (_cacheLock) { _bitmapCache[path] = bmp; }
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    void TrimAndPreload(string[] files, int index)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { files[index] };
        if (index > 0) keep.Add(files[index - 1]);
        if (index < files.Length - 1) keep.Add(files[index + 1]);

        // Evict entries not in the 3-entry window
        lock (_cacheLock)
        {
            var toRemove = new List<string>();
            foreach (var k in _bitmapCache.Keys)
                if (!keep.Contains(k)) toRemove.Add(k);
            foreach (var k in toRemove)
            {
                //_bitmapCache[k].Dispose();
                _bitmapCache.Remove(k);
            }
        }

        // Preload neighbors on background threads
        if (index < files.Length - 1) PreloadIfNeeded(files[index + 1]);
        if (index > 0) PreloadIfNeeded(files[index - 1]);
    }

    void PreloadIfNeeded(string path)
    {
        lock (_cacheLock)
        {
            if (_bitmapCache.ContainsKey(path)) return;
        }
        Task.Run(() =>
        {
            try
            {
                var bmp = new Bitmap(path);
                lock (_cacheLock)
                {
                    if (!_bitmapCache.ContainsKey(path))
                        _bitmapCache[path] = bmp;
                    else
                        bmp.Dispose();
                }
            }
            catch { }
        });
    }

    void ClearCache()
    {
        lock (_cacheLock)
        {
            foreach (var bmp in _bitmapCache.Values) bmp.Dispose();
            _bitmapCache.Clear();
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Text == null) return;

        if (!_searchMode && e.Text == "/")
        {
            _searchMode = true;
            _searchQuery = "";
            Redraw();
            e.Handled = true;
            return;
        }

        if (_searchMode)
        {
            _searchQuery += e.Text;
            ApplySearch();
            Redraw();
            e.Handled = true;
        }
    }

    void ApplySearch()
    {
        var currentFile = _files.Length > 0 && _index < _files.Length ? _files[_index] : null;

        var filtered = string.IsNullOrEmpty(_searchQuery)
            ? (string[])_allFiles.Clone()
            : _allFiles.Where(f => Path.GetRelativePath(_folder, f)
                .Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)).ToArray();

        _files = filtered;
        if (_files.Length > 0)
        {
            SortItems(_files, _sortMode);
            var idx = currentFile != null ? Array.IndexOf(_files, currentFile) : -1;
            _index = idx >= 0 ? idx : 0;
        }
        else
        {
            _index = 0;
        }
        ResetAlt();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_files.Length == 0 && !_searchMode) return;

        // If pending delete confirmation, any key confirms
        if (_pendingDelete)
        {
            _pendingDelete = false;
            Redraw();
            e.Handled = true;
            return;
        }

        // Search mode key handling
        if (_searchMode)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _searchMode = false;
                    _searchQuery = "";
                    _files = (string[])_allFiles.Clone();
                    ApplySort(_files, _allFiles, _sortMode, _groupByFolder);
                    if (_index >= _files.Length) _index = _files.Length - 1;
                    if (_index < 0) _index = 0;
                    ResetAlt();
                    Redraw();
                    e.Handled = true;
                    return;
                case Key.Back:
                    if (_searchQuery.Length > 0)
                    {
                        _searchQuery = _searchQuery[..^1];
                        ApplySearch();
                    }
                    Redraw();
                    e.Handled = true;
                    return;
                case Key.Enter:
                {
                    // Commit: keep the file the search landed on, and go back to browsing everything.
                    //
                    // The filtered list cannot simply be left in place. ApplySort rebuilds `files` by
                    // copying `allFiles.Length` entries into it, so a shorter array throws the moment
                    // anything re-sorts -- D, N, S, G or '.' -- which is one keystroke AFTER leaving
                    // search and looks nothing like a search bug. Escape already restores the full list
                    // for this reason; Enter did not.
                    var found = _files.Length > 0 && _index < _files.Length ? _files[_index] : null;

                    _searchMode = false;
                    _searchQuery = "";
                    _files = (string[])_allFiles.Clone();
                    ApplySort(_files, _allFiles, _sortMode, _groupByFolder);

                    var restored = found is null ? -1 : Array.IndexOf(_files, found);
                    _index = restored >= 0 ? restored : 0;

                    ResetAlt();
                    Redraw();
                    e.Handled = true;
                    return;
                }
                case Key.Right:
                    if (_files.Length > 0 && _index < _files.Length - 1)
                    {
                        _index++;
                        ResetAlt();
                        Redraw();
                    }
                    e.Handled = true;
                    return;
                case Key.Left:
                    if (_files.Length > 0 && _index > 0)
                    {
                        _index--;
                        ResetAlt();
                        Redraw();
                    }
                    e.Handled = true;
                    return;
                default:
                    // Don't mark handled — let OnTextInput receive the character
                    return;
            }
        }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.Right:
                if (_index < _files.Length - 1)
                {
                    _index++;
                    ResetAlt();
                    Redraw();
                    RestartAutoTimer();
                }
                e.Handled = true;
                break;

            case Key.Left:
                if (_index > 0)
                {
                    _index--;
                    ResetAlt();
                    Redraw();
                    RestartAutoTimer();
                }
                e.Handled = true;
                break;

            case Key.Down:
            case Key.Up:
                HandleAltCycle(e.Key == Key.Down);
                e.Handled = true;
                break;

            case Key.Space:
                _autoAdvance = !_autoAdvance;
                if (_autoAdvance)
                    _timer?.Start();
                else
                    _timer?.Stop();
                Redraw();
                e.Handled = true;
                break;

            case Key.Q:
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Home:
                if (shift)
                {
                    _index = 0;
                }
                else
                {
                    string curDir = Path.GetDirectoryName(_files[_index])!;
                    for (int i = 0; i < _files.Length; i++)
                    {
                        if (Path.GetDirectoryName(_files[i])!.Equals(curDir, StringComparison.OrdinalIgnoreCase))
                        { _index = i; break; }
                    }
                }
                ResetAlt(); Redraw(); RestartAutoTimer();
                e.Handled = true;
                break;

            case Key.End:
                if (shift)
                {
                    _index = _files.Length - 1;
                }
                else
                {
                    string curDir = Path.GetDirectoryName(_files[_index])!;
                    for (int i = _files.Length - 1; i >= 0; i--)
                    {
                        if (Path.GetDirectoryName(_files[i])!.Equals(curDir, StringComparison.OrdinalIgnoreCase))
                        { _index = i; break; }
                    }
                }
                ResetAlt(); Redraw(); RestartAutoTimer();
                e.Handled = true;
                break;

            case Key.PageDown:
            {
                string curDir = Path.GetDirectoryName(_files[_index])!;
                for (int i = _index + 1; i < _files.Length; i++)
                {
                    if (!Path.GetDirectoryName(_files[i])!.Equals(curDir, StringComparison.OrdinalIgnoreCase))
                    { _index = i; break; }
                }
                ResetAlt(); Redraw(); RestartAutoTimer();
                e.Handled = true;
                break;
            }

            case Key.PageUp:
            {
                string curDir = Path.GetDirectoryName(_files[_index])!;
                int groupStart = _index;
                while (groupStart > 0 && Path.GetDirectoryName(_files[groupStart - 1])!.Equals(curDir, StringComparison.OrdinalIgnoreCase))
                    groupStart--;
                if (groupStart > 0)
                {
                    string prevDir = Path.GetDirectoryName(_files[groupStart - 1])!;
                    int prevStart = groupStart - 1;
                    while (prevStart > 0 && Path.GetDirectoryName(_files[prevStart - 1])!.Equals(prevDir, StringComparison.OrdinalIgnoreCase))
                        prevStart--;
                    _index = prevStart;
                }
                ResetAlt(); Redraw(); RestartAutoTimer();
                e.Handled = true;
                break;
            }

            case Key.G:
            {
                string currentFile = _files[_index];
                _groupByFolder = !_groupByFolder;
                ApplySort(_files, _allFiles, _sortMode, _groupByFolder);
                _index = Array.IndexOf(_files, currentFile);
                ResetAlt();
                Redraw();
                e.Handled = true;
                break;
            }

            case Key.D:
            {
                string currentFile = _files[_index];
                _sortMode = shift ? SortMode.DateAsc : SortMode.Date;
                ApplySort(_files, _allFiles, _sortMode, _groupByFolder);
                _index = Array.IndexOf(_files, currentFile);
                ResetAlt();
                Redraw();
                e.Handled = true;
                break;
            }

            case Key.N:
            {
                string currentFile = _files[_index];
                _sortMode = shift ? SortMode.NameDesc : SortMode.Name;
                ApplySort(_files, _allFiles, _sortMode, _groupByFolder);
                _index = Array.IndexOf(_files, currentFile);
                ResetAlt();
                Redraw();
                e.Handled = true;
                break;
            }

            case Key.S:
            {
                string currentFile = _files[_index];
                _sortMode = shift ? SortMode.SizeAsc : SortMode.Size;
                ApplySort(_files, _allFiles, _sortMode, _groupByFolder);
                _index = Array.IndexOf(_files, currentFile);
                ResetAlt();
                Redraw();
                e.Handled = true;
                break;
            }

            case Key.OemPeriod:
            {
                string currentFile = _files[_index];
                _sortMode = _sortMode == SortMode.Random ? SortMode.Name : SortMode.Random;
                ApplySort(_files, _allFiles, _sortMode, _groupByFolder);
                _index = Array.IndexOf(_files, currentFile);
                ResetAlt();
                Redraw();
                e.Handled = true;
                break;
            }

            case Key.O:
                Process.Start(new ProcessStartInfo(DisplayPath()) { UseShellExecute = true });
                e.Handled = true;
                break;

            case Key.U:
            {
                if (_upscaling) break;
                var src = _files[_index];
                var ext = Path.GetExtension(src);
                var dst = Path.Combine(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + "_resized" + ext);
                if (File.Exists(dst)) break;
                _upscaling = true;
                var fileName = Path.GetFileName(src);
                int lines = 0;
                Header.Text = $" Upscaling {fileName}  {ProgressBar(0, 10)}  0%";
                var psi = new ProcessStartInfo("upscayl", $"-i \"{src}\" -o \"{dst}\" -n upscayl-lite-4x -s 2")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                _ = Task.Run(() =>
                {
                    var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        void OnData(object sender, DataReceivedEventArgs args)
                        {
                            if (args.Data == null) return;
                            int n = Math.Min(Interlocked.Increment(ref lines), 10);
                            int pct = n * 100 / 10;
                            Dispatcher.UIThread.Post(() =>
                                Header.Text = $" Upscaling {fileName}  [{ProgressBar(n, 10)}] {pct,3}%");
                        }
                        proc.OutputDataReceived += OnData;
                        proc.ErrorDataReceived += OnData;
                        proc.BeginOutputReadLine();
                        proc.BeginErrorReadLine();
                        proc.WaitForExit();
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        _upscaling = false;
                        Header.Text = $" Upscaling {fileName}  {ProgressBar(10, 10)}  100%";
                        if (File.Exists(dst) && !_allFiles.Contains(dst))
                        {
                            _allFiles = _allFiles.Append(dst).ToArray();
                            Array.Sort(_allFiles, StringComparer.OrdinalIgnoreCase);
                            _files = _files.Take(_index + 1).Append(dst).Concat(_files.Skip(_index + 1)).ToArray();
                            _index++;
                            ResetAlt();
                            Redraw();
                        }
                    });
                });
                e.Handled = true;
                break;
            }

            case Key.R:
            {
                var cur = _files[_index];
                var nameNoExt = Path.GetFileNameWithoutExtension(cur);
                if (nameNoExt.EndsWith("_resized"))
                {
                    var ext = Path.GetExtension(cur);
                    var baseName = nameNoExt[..^"_resized".Length];
                    var original = Path.Combine(Path.GetDirectoryName(cur)!, baseName + ext);
                    var originalFileName = baseName + ext;
                    // Release cached bitmaps before file operations
                    ImageView.Source = null;
                    ClearCache();

                    // Step 1: Replace original with resized version
                    File.Delete(original);
                    File.Move(cur, original);
                    _allFiles = _allFiles.Where(f => f != cur).ToArray();
                    _files = _files.Where(f => f != cur).ToArray();

                    // Step 2: Upscale all alternates
                    var alternates = _allFiles.Where(f => f != original
                        && Path.GetFileName(f).Equals(originalFileName, StringComparison.OrdinalIgnoreCase)).ToArray();

                    if (shift)
                    {
                        // Shift+R: fire-and-forget parallel upscale
                        foreach (var alt in alternates)
                        {
                            var altCopy = alt;
                            var altDir = Path.GetDirectoryName(altCopy)!;
                            var tmpDst = Path.Combine(altDir, baseName + "_tmp_upscale" + ext);
                            _ = Task.Run(() =>
                            {
                                var psi = new ProcessStartInfo("upscayl", $"-i \"{altCopy}\" -o \"{tmpDst}\" -n upscayl-lite-4x -s 2")
                                {
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                };
                                var proc = Process.Start(psi);
                                proc?.WaitForExit();
                                if (File.Exists(tmpDst))
                                {
                                    File.Delete(altCopy);
                                    File.Move(tmpDst, altCopy);
                                }
                            });
                        }
                    }
                    else
                    {
                        // R: batch upscale via temp folder
                        BatchUpscaleAlternates(alternates, ext);
                    }

                    // Move to next image immediately
                    if (_index >= _files.Length) _index = _files.Length - 1;
                    ResetAlt();
                    ClearCache();
                    Redraw();
                }
                e.Handled = true;
                break;
            }

            case Key.Delete:
            {
                string fileToDel = _files[_index];
                bool isResized = Path.GetFileNameWithoutExtension(fileToDel).EndsWith("_resized");

                // Release cached bitmaps before deleting
                ImageView.Source = null;
                ClearCache();

                if (isResized)
                {
                    File.Delete(fileToDel);
                    Header.Text = $"Deleted: {fileToDel}  (press any key)";
                }
                else
                {
                    var toDelete = _allFiles.Where(f => Path.GetFileName(f) == Path.GetFileName(fileToDel)).ToList();
                    foreach (var file in toDelete)
                        File.Delete(file);
                    Header.Text = $"Deleted {toDelete.Count} file(s): {Path.GetFileName(fileToDel)}  (press any key)";
                }
                _pendingDelete = true;

                _allFiles = _allFiles.Where(f => f != fileToDel).ToArray();
                _files = _files.Where(f => f != fileToDel).ToArray();
                ResetAlt();
                if (_files.Length == 0) { Close(); return; }
                if (isResized && _index > 0) _index--;
                else if (_index >= _files.Length) _index = _files.Length - 1;
                RestartAutoTimer();
                e.Handled = true;
                break;
            }
        }
    }

    void HandleAltCycle(bool forward)
    {
        string currentFile = _files[_index];
        string currentName = Path.GetFileName(currentFile);
        if (_altBasePath != currentFile)
        {
            _altFiles = _allFiles.Where(f => Path.GetFileName(f).Equals(currentName, StringComparison.OrdinalIgnoreCase)).ToArray();
            _altBasePath = currentFile;
            _altIndex = -1;
        }
        if (_altFiles.Length > 1)
        {
            int cur = _altIndex >= 0 ? _altIndex : Array.IndexOf(_altFiles, currentFile);
            _altIndex = forward
                ? (cur + 1) % _altFiles.Length
                : (cur - 1 + _altFiles.Length) % _altFiles.Length;
            Redraw();
        }
    }

    static string ProgressBar(int current, int total, int width = 20)
    {
        int filled = total > 0 ? current * width / total : 0;
        return new string('\u2588', filled) + new string('\u2591', width - filled);
    }

    void BatchUpscaleAlternates(string[] alternates, string ext)
    {
        if (alternates.Length == 0) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "gixel_batch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        // Move alternates into temp folder with indexed names to avoid collisions
        var mapping = new Dictionary<string, string>(); // tempPath -> originalPath
        for (int i = 0; i < alternates.Length; i++)
        {
            var tempPath = Path.Combine(tempDir, $"{i}{ext}");
            File.Move(alternates[i], tempPath);
            mapping[tempPath] = alternates[i];
        }

        var count = alternates.Length;
        Header.Text = $" Batch upscaling {count} alternate(s)...";

        _ = Task.Run(() =>
        {
            var psi = new ProcessStartInfo("upscayl", $"-i \"{tempDir}\" -o \"{tempDir}\" -n upscayl-lite-4x -s 2")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit();

            // Move upscaled files back to original locations
            foreach (var (tempPath, originalPath) in mapping)
            {
                if (File.Exists(tempPath))
                    File.Move(tempPath, originalPath);
            }

            // Clean up temp folder
            try { Directory.Delete(tempDir, true); } catch { }

            Dispatcher.UIThread.Post(() =>
            {
                ClearCache();
                Redraw();
            });
        });
    }

    void RestartAutoTimer()
    {
        if (_autoAdvance)
        {
            _timer?.Stop();
            _timer?.Start();
        }
    }

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
            case SortMode.Size:
                var sizes = new Dictionary<string, long>(items.Length, StringComparer.OrdinalIgnoreCase);
                foreach (var f in items) sizes[f] = new FileInfo(f).Length;
                Array.Sort(items, (a, b) => sizes[b].CompareTo(sizes[a]));
                break;
            case SortMode.SizeAsc:
                var sizesAsc = new Dictionary<string, long>(items.Length, StringComparer.OrdinalIgnoreCase);
                foreach (var f in items) sizesAsc[f] = new FileInfo(f).Length;
                Array.Sort(items, (a, b) => sizesAsc[a].CompareTo(sizesAsc[b]));
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
}

enum SortMode { Random, Name, NameDesc, Date, DateAsc, Size, SizeAsc }
