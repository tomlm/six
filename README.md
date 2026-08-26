[![Build and Test](https://github.com/tomlm/Six/actions/workflows/BuildAndRunTests.yml/badge.svg)](https://github.com/tomlm/Six/actions/workflows/BuildAndRunTests.yml)[![NuGet](https://img.shields.io/nuget/v/Six.svg)](https://www.nuget.org/packages/Six)

![Logo](https://raw.githubusercontent.com/tomlm/Six/refs/heads/main/icon.png)

# Six

![Screenshot](https://raw.githubusercontent.com/tomlm/Six/refs/heads/main/screenshot.gif)

A terminal image viewer that renders images using [Sixel](https://en.wikipedia.org/wiki/Sixel) graphics, allowing you to browse a folder full of images directly in your terminal.

## Features

- Renders images inline in the terminal via Sixel
- Browses all images in a folder (and subfolders)
- Multiple sort modes: random, name, name descending, date, date ascending
- Optional folder-grouping mode
- Auto-advance slideshow
- Alt-file cycling (compare same filename across different folders)
- Preloads neighboring images for fast navigation
- Image upscaling via [upscayl](https://github.com/upscayl/upscayl-ncnn) (`U`)
- Delete files directly from the viewer
- Open images in the default OS viewer (`O`)

## Supported Formats

`.png`, `.jpg`, `.jpeg`, `.bmp`, `.gif`, `.webp`, `.ico`, `.tiff`, `.tif`

## Requirements

- A terminal emulator with Sixel support (e.g. [Windows Terminal](https://github.com/microsoft/terminal), iTerm2, foot, mlterm, XTerm)
- [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) runtime

## Installation

Install as a .NET global tool:

```bash
dotnet tool install -g Six
```


## Usage

```bash
six <folder>
```

Recursively scans `<folder>` for images and opens the viewer.

## Key Bindings

| Key | Action |
|-----|--------|
| `←` / `→` | Previous / Next image |
| `↑` / `↓` | Cycle alternate files with the same filename across folders |
| `Home` | First image in current folder group |
| `End` | Last image in current folder group |
| `Shift+Home` | Very first image |
| `Shift+End` | Very last image |
| `PageDown` | First image of next folder |
| `PageUp` | First image of previous folder |
| `Space` | Toggle auto-advance slideshow (3 s interval) |
| `N` | Sort by name (ascending) |
| `Shift+N` | Sort by name (descending) |
| `D` | Sort by date (descending) |
| `Shift+D` | Sort by date (ascending) |
| `.` | Toggle random / name sort |
| `G` | Toggle group-by-folder mode |
| `O` | Open image in default OS viewer |
| `U` | Upscale image with upscayl (`_resized` copy) |
| `R` | Rename `_resized` copy back to original name |
| `Delete` | Delete current image (and all copies with same filename) |
| `Q` / `Escape` | Quit |

## Building from Source

```bash
git clone https://github.com/tomlm/six.git
cd six/src/Six
dotnet build
```

To pack as a tool:

```bash
dotnet pack
```

## Dependencies

- [SkiaSharp](https://github.com/mono/SkiaSharp) — image decoding and scaling
- [JeremyAnsel.ColorQuant](https://github.com/JeremyAnsel/JeremyAnsel.ColorQuant) — color quantization for Sixel palette

## License

MIT
