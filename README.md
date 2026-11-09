# windows-mods

Small tools and tweaks for Windows 11 and the Claude desktop app. Larger projects have their own repos: [cmdtab fork](https://github.com/doruk-akcali/cmdtab) (macOS-style Alt+Tab with Dock zoom, slide and liquid glass) and [ShotPeek](https://github.com/doruk-akcali/shotpeek) (macOS-style screenshot preview).

| Folder | What it is |
|---|---|
| [`crab-eta/`](crab-eta) | Claude Code mod: during long tasks, a Claude crab at the bottom-right of the screen shows the minutes left; its colour follows the 5-hour usage and the flag in its claw the weekly usage |
| [`claude-game-mode/`](claude-game-mode) | Ctrl+Alt+G shrinks the Claude app's CPU and RAM use (below-normal priority, efficiency mode, RAM cap) without closing it, so remote chats stay connected while gaming |
| [`cursor-resizer/`](cursor-resizer) | Redraws a Windows cursor pack (`.cur` / `.ani`) smaller, keeping every frame sharp and the click point exact — used to make the [apple_cursor](https://github.com/ful1e5/apple_cursor) macOS pointers 85% size |
| [`scripts/`](scripts) | `lockout-report.ps1`: saves the last 24 h of failed sign-ins and account lockouts from the Security log (admin terminal) |

## crab-eta

A Claude Code plugin. Load it by adding its folder to `CLAUDE_CODE_PLUGIN_DIRS` in the `env` block of `~/.claude/settings.json`. It registers an `eta` tool Claude calls with its estimate; `hooks/register.ts` writes the state, `popup/crab-popup.ps1` draws the crab. `/crab-test 5` shows it for 20 seconds.

## claude-game-mode

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize /out:ClaudeGameMode.exe ClaudeGameMode.cs
ClaudeGameMode.exe listen     # stay in the background, toggle on Ctrl+Alt+G
ClaudeGameMode.exe on|off|toggle
```

## cursor-resizer

Runs in Windows PowerShell 5.1 (System.Drawing). The scale (0.85) is set in `curscale.ps1`.

```
powershell.exe -File curscale.ps1 CurScale.cs <input-folder> <output-folder> <preview.png>
```

Then point Settings → Mouse pointer (or `HKCU\Control Panel\Cursors`) at the output folder. Cursor packs keep their own licence (apple_cursor is GPL-3.0), so this repo ships the tool, not the cursors.
