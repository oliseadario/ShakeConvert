# Contributing

Thanks for wanting to help ✦

## Getting started

1. Fork and clone the repo on Windows 10/11.
2. Install the .NET 8 SDK and, if you need them, the optional tools listed in the README (FFmpeg, 7-Zip, Python engines).
3. Use `dotnet run` to try your changes.

## Guidelines

- **Local only.** Conversions must never upload files or call online APIs.
- **Feel native.** UI should follow Windows 11 / WinUI conventions: theme, accent color, transparency setting.
- **Translate strings.** Wrap user-facing text in `L.T("italiano", "English")`.
- **Never touch the original.** Write results next to the source file.
- Keep pull requests focused. For a new format, add it to the catalog in `Converters.cs` or `MoreFormats.cs` and list it in the README.

## Reporting bugs

Open an issue using the bug template. The error log in `%AppData%\ShakeConvert` helps a lot; check it for private file names before attaching it.
