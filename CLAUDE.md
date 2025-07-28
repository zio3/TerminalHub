# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Development Commands

### Build
```bash
dotnet build
dotnet build TerminalHub/TerminalHub.csproj
```

### Run
```powershell
# Start with automatic browser launch (background)
./start.ps1

# Start in foreground mode
./start.ps1 -Foreground

# Start without browser
./start.ps1 -NoBrowser

# Stop the running server
./stop.ps1
```

### Clean
```bash
dotnet clean
```

## Architecture Overview

TerminalHub is a Blazor Server application that provides a web-based terminal interface with support for multiple terminal sessions, including Windows ConPTY integration.

### Core Components

1. **Terminal Management**
   - `ConPtyService`: Windows ConPTY API wrapper for creating pseudo-console sessions
   - `ConPtyWithBuffer`: Combines ConPTY session with circular buffer for output management
   - `SessionManager`: Manages multiple terminal sessions, handles lazy initialization
   - `TerminalService`: Abstracts JavaScript interop for XTerm.js terminal operations

2. **Session Types**
   - Regular Terminal Sessions
   - Claude Code CLI Sessions (with special output analysis)
   - Gemini CLI Sessions (with output analysis)
   - DOS Command Sessions
   - Task Runner Sessions (npm scripts)

3. **UI Architecture**
   - `Root.razor`: Main component (1300+ lines) that orchestrates the UI
   - Session list on the left, terminal display on the right
   - Bottom panel with tabs for different session types
   - Real-time terminal output with XTerm.js

4. **Key Services**
   - `OutputAnalyzerService`: Analyzes CLI output for Claude Code/Gemini, tracks processing status
   - `InputHistoryService`: Manages command history with persistence
   - `GitService`: Git operations including worktree management
   - `PackageJsonService`: Reads npm scripts from package.json files
   - `LocalStorageService`: Browser local storage persistence
   - `NotificationService`: Cross-session notifications

### Important Implementation Details

1. **Lazy Session Initialization**: ConPTY sessions are only created when first accessed to improve performance
2. **Circular Buffer**: Each session maintains a CircularLineBuffer for efficient output storage
3. **XTerm.js Integration**: Terminal rendering uses XTerm.js with custom Windows-specific settings
4. **Git Worktree Support**: Sessions can create git worktrees, placed as siblings to parent directory
5. **Output Analysis**: Real-time parsing of Claude Code/Gemini CLI output to track token usage and processing time

### JavaScript Files
- `wwwroot/js/terminal.js`: XTerm.js initialization, terminal management, resize handling
- `wwwroot/js/helpers.js`: Utility functions for DOM manipulation, local storage

### Common Issues and Solutions

1. **Terminal Display Issues**: Check `term.onData()` handler in terminal.js - may cause double character display
2. **Build Errors with OutputAnalyzerService**: Ensure `activeSessionId` parameter is passed to `AnalyzeOutput` method
3. **Worktree Path Issues**: SessionManager now strips trailing directory separators before creating worktrees
4. **UTF-8 Decode Errors**: Fixed in ConPtySession.ReadAsync with proper buffer size calculation

### Session Notification System
- Sessions track processing completion with elapsed time and token count
- Non-active sessions show notification bell when processing completes
- Notification logic checks `activeSessionId` to determine if session is active