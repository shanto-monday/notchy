# Notchy

A MacBook-style notch for Windows. Move your mouse to the top middle of the screen and it opens.

**[Download Notchy.exe](../../releases/latest)**, double-click it and you're done. Nothing to install, no account. It updates itself.

## What's inside
- **Now playing**: song, artwork, play/pause/skip and seek for Spotify, YouTube, any app in the Windows media flyout. Shows a small pop-up when the song changes.
- **Volume mixer**: master volume plus a slider, mute button and live level for every app making sound.
- **System**: CPU, RAM, NVIDIA GPU load, temperature and VRAM, network speed, disk space, battery, and the apps using the most memory.
- **Clipboard history**: search, pin and click to paste. Skips passwords from password managers. Images stay in memory only.
- **Shelf**: drop files on the notch, drag them out later.
- **Notes**: a scratchpad that saves by itself.
- **Timer and stopwatch**, with a pop-up when time is up.
- **AI usage**: Codex 5-hour and weekly limits, Claude Code replies and tokens for today and the current 5-hour window. Read from the logs on your PC.
- **Quick toggles**: keep awake, mute mic, dark mode, screenshot, show desktop, screen off, lock, empty Recycle Bin.

Ctrl + Alt + N opens or closes it. It hides itself during fullscreen games and videos. Press Esc to close it.

## Needs
Windows 10 (2004+) or 11, 64-bit. Uses Microsoft Edge WebView2, which Windows already has.

## Build
.NET 8 SDK: `dotnet publish -c Release -o dist` gives one `dist\Notchy.exe`. Release: `release.ps1 -Version 1.1.0 -Notes "..."`.
QA: `Notchy.exe --shot out.png [home|mixer|system|clipboard|shelf|notes|ai|settings]`.

Inspired by the Dynamic Island in [Vorssaint](https://github.com/vorssaint/vorssaint-utils) (Mac). Notchy is its own app, written from scratch for Windows.
