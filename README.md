# TerminalHub

**English** | [日本語](README.ja.md)

[![GitHub Release](https://img.shields.io/github/v/release/zio3/TerminalHub)](https://github.com/zio3/TerminalHub/releases/latest)
[![License](https://img.shields.io/badge/license-ISC-blue.svg)](LICENSE)

**A Windows-native GUI for managing multiple AI CLI sessions — with first-class IME support for CJK and other composition-based input.**

🌐 **[Landing Page](https://zio3.github.io/TerminalHub/landing/)** · 📥 **[Download](https://github.com/zio3/TerminalHub/releases/latest)** · 💬 **[Discord](https://discord.gg/juCcFNXJg7)**

TerminalHub is a Windows desktop application that brings multi-session management to Claude Code / Gemini CLI / Codex CLI through a single GUI. It uses the Windows ConPTY API to deliver session management, status monitoring, and notifications — without needing `tmux`.

Just run the installer — no .NET installation required.

---

## Why TerminalHub?

On macOS / Linux you can manage multiple sessions with `tmux` + tools like `claude-tmux`. On Windows native, `tmux` isn't available and session management choices are limited. **For developers who type in CJK (Japanese, Chinese, Korean) or other composition-based scripts, most Windows terminals also struggle with IME — a long-standing pain point that TerminalHub avoids by rendering via XTerm.js in the browser.**

| Problem | How TerminalHub Solves It |
|---------|--------------------------|
| IME composition (Japanese / Chinese / Korean input) breaks or flickers in most terminals | Full IME support through XTerm.js in-browser rendering |
| Windows Terminal tabs don't show session name or status at a glance | Unified sidebar with session name, processing state, and elapsed time |
| No `tmux` on Windows means no session manager | GUI for creating, switching, and archiving sessions |
| Staring at the screen waiting for a long task to finish | Completion notifications + Webhook integration |
| Git worktree management is all manual | One-click worktree session creation from the GUI |
| Remembering the right flags for each AI CLI | Checkbox-based option configuration (approval mode, resume, etc.) |

---

## Features

### Multi-Session Management

- Run as many terminal sessions as you want (no hard limit)
- Named sessions with memos and search filtering
- Automatic session state save / restore
- Session archive / restore / bulk delete
- Multi-browser support — connect to the same session from multiple browsers

### AI CLI Integration

| Feature | Claude Code | Gemini CLI | Codex CLI |
|---------|:-----------:|:----------:|:---------:|
| Session management | ✓ | ✓ | ✓ |
| Real-time status detection | ✓ | ✓ | – |
| Token usage / elapsed time display | ✓ | ✓ | – |
| Completion notification | ✓ | ✓ | ✓ |
| GUI option configuration | ✓ | ✓ | ✓ |

- Processing / idle / input-waiting states are detected and displayed in real time
- Inactive sessions show a notification bell when processing finishes
- Webhook notifications forward events to your phone or other services

### Git Integration

- Automatic Git repository detection and branch display
- Uncommitted changes indicator
- Git worktree session creation from the GUI
- Parent-child relationship visualization between parent sessions and worktree sessions

### Webhook Notifications

Session start / complete events can be forwarded externally.

```json
{
  "eventType": "complete",
  "sessionName": "session name",
  "terminalType": "ClaudeCode",
  "elapsedSeconds": 123,
  "timestamp": "2025-01-01T00:00:00Z",
  "folderPath": "C:\\path\\to\\folder"
}
```

### Other

- Command history (navigate with Ctrl+Up / Ctrl+Down)
- Automatic URL detection and click-to-open inside the terminal
- Warnings when a session's working directory no longer exists
- Toast notifications for session initialization errors

---

## System Requirements

- Windows 10 / 11

### For development
- .NET 10.0 SDK
- Node.js (optional)

## Installation

### Using the installer (recommended)

1. [Download the latest release](https://github.com/zio3/TerminalHub/releases/latest)
2. Run `TerminalHub-Setup-x.x.x.exe`
3. Launch from the Start menu or desktop shortcut after installation

### From source (for developers)

```powershell
git clone https://github.com/zio3/TerminalHub.git
cd TerminalHub
dotnet run --project TerminalHub/TerminalHub.csproj

# or with npm
npm start
```

## Usage

### Creating a session
1. Click the "New Session" button
2. Pick a working directory
3. Choose a session type (Terminal / Claude Code / Gemini CLI / Codex CLI)
4. Set options as needed

### Managing sessions
- Browse / switch sessions from the left sidebar
- Narrow the list with the search box (by name or memo)
- Open the gear icon for memos and archiving
- Right-click to create a worktree session

### Keyboard shortcuts
| Key | Action |
|-----|--------|
| `Ctrl + Up/Down` | Navigate command history |
| `Ctrl + C` | Copy selected text (or interrupt when nothing is selected) |
| `Ctrl + V` | Paste |

## Tech Stack

- **Frontend**: Blazor Server, XTerm.js
- **Backend**: ASP.NET Core (.NET 10.0)
- **Terminal**: Windows ConPTY API
- **JavaScript**: XTerm.js, WebLinksAddon
- **Styling**: Bootstrap 5
- **Installer**: Inno Setup

## Development

See [CLAUDE.md](CLAUDE.md) for development details.

## Troubleshooting

### The terminal doesn't show up
- Check that you're running Windows 10 / 11
- Check the browser console for errors

### Sessions don't persist
- Make sure browser local storage is enabled
- Private / incognito browsing mode disables persistence

## License

ISC License

## Author

akihiro taguchi (info@zio3.net)

## Contributing

Pull requests are welcome. For significant changes, please open an issue first to discuss what you'd like to change.
