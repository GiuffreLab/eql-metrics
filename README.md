# EQL Metrics

A real-time, movable, transparent overlay for **EverQuest Legends** that parses your
character log file and shows live combat and session metrics: player / pet / combined
DPS, a per-ability damage breakdown, healing, currency per hour, motes per hour,
XP per hour and time-to-level, kills, damage taken, and a live loot feed.

Built with C# / .NET 10 (WPF). Windows only.

---

## Build & run

You need the **.NET 10 SDK** (https://dotnet.microsoft.com/download/dotnet/10.0). Then,
from this folder:

```powershell
dotnet run -c Release
```

The first launch will try to auto-detect your newest `eqlog_*.txt` under
`E:\EverQuest Legends\Logs`. If it can't find one, click the **folder icon** in the
title bar and pick your character log.

### Make a standalone .exe (no SDK needed to run)

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The exe lands in `bin\Release\net10.0-windows\win-x64\publish\EqlMetrics.exe`.

---

## Enable logging in EverQuest

The overlay reads the game's own log file. In-game, turn logging on with:

```
/log on
```

Your log is written to `...\EverQuest Legends\Logs\eqlog_<Character>_<server>.txt`.
The overlay follows it live while you play.

---

## Using the overlay

Title-bar buttons (left to right):

| Icon | Action |
|------|--------|
| 📁   | Pick a different log file |
| ↻    | Reset the current session's stats |
| ☾ / ☀ | More transparent / more solid |
| ⊘    | Toggle **click-through** (mouse passes through to the game) |
| ▢    | Expand / collapse (minimized vs. full breakdown) |
| ✕    | Close |

- **Drag** the window by its title bar. Position, size, and opacity are remembered.
- **Click-through** can also be toggled with the global hotkey **Ctrl+Alt+X** — handy
  because while click-through is on you can't click the buttons.
- Click any combatant row to see that combatant's ability breakdown.

### Pet classes

Player damage (logged as "You") is detected automatically. Pets are logged under their
own name, which the game randomizes, so set it once: edit
`%APPDATA%\EqlMetrics\settings.json` and set `"PetName": "YourPetName"`, then restart.
The Pet and Combined DPS lines then populate automatically. (Your Cleric has no pet, so
those stay hidden.)

---

## Project layout

```
EqlMetrics.csproj        WPF project
App.xaml / App.xaml.cs   application entry
MainWindow.xaml(.cs)     the overlay window + live UI
Core/
  CombatModel.cs         combatant / ability / loot data types
  CombatParser.cs        log-line regexes + session aggregation  (engine)
  LogTailer.cs           real-time shared-read file tailer
  Settings.cs            persisted user settings
```

The `Core` classes have **no WPF dependency** — the parser was developed and unit-tested
against a real log before the UI was built, so the numbers are trustworthy and the
engine can be reused (e.g. for a CLI or tests).

---

## What it tracks (and the log lines behind it)

| Metric | Source line example |
|--------|---------------------|
| Melee DPS (by skill) | `You pierce orc centurion for 8 points of damage.` |
| Spell nuke | `You hit orc centurion for 13 points of magic damage by Suffocating Sphere.` |
| DoT | `Orc centurion has taken 9 damage from your Suffocating Sphere.` |
| Group / pet DPS | same lines, keyed to the actor's name |
| Healing / HPS | `You healed Ztik for 1 (16) hit points by Courage.` |
| Currency / hr | `You receive 7 copper from the corpse.` / `sold it for 2 silver and 9 copper.` |
| Motes / hr | `You looted a Glowing mote of potential ...` |
| XP / hr, time-to-level | `You gain experience! (0.986%)` |
| Kills / hr | `Orc centurion has been slain by Ztik!` |
| Damage taken | `Orc centurion hits YOU for 1 point of damage.` |
