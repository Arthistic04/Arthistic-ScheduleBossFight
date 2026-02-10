# Scheduled Boss Fight

A **Valheim** BepInEx mod that unlocks bosses on real-life dates. Set a date and time (Philippines, GMT+8) for each boss; the mod blocks altar use until then and can notify players via in-game chat and optional Discord webhooks.

---

## Features

- **Date-based unlock** — Configure unlock utc offset/date/time per boss (Eikthyr, Elder, Bonemass, Moder, Yagluth, Queen, Fader).
- **Blocks early summon** — Prevents using boss altars or offering items before the scheduled unlock.
- **In-game messages** — Countdown and “unlocks tomorrow” / “unlocked” messages to all connected players.
- **Optional Discord** — Send the same announcements to a Discord channel via webhook.
- **Config sync** — ServerSync support so server config is synced to clients (optional lock for admin-only changes).
- **Config watcher** — Edit the config file on disk and the mod reloads it without restarting the server.

---

## Requirements

- **Valheim** (dedicated server and/or client)
- **BepInEx 5** (e.g. [BepInExPack Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/))

ServerSync is included with the mod for config synchronization.

---

## Installation

1. Install BepInEx and Jotunn (see links above) if you haven’t already.
2. Install **Scheduled Boss Fight** via your mod manager (e.g. r2modman, Thunderstore Mod Manager) or manually:
   - Extract the package into your Valheim folder so that `BepInEx/plugins/Arthistic-ScheduleBossFight/` contains the mod DLL (and any bundled files).
3. Run the game or dedicated server once to generate the config file.

---

## Configuration

Config file: `BepInEx/config/arthistic_scheduledbossfight.cfg`

### General

| Setting | Default | Description |
|--------|---------|-------------|
| **Lock Configuration** | `true` | If enabled, only server admins can change config (synced via ServerSync). |
| **CheckIntervalSeconds** | `1800` | How often the mod checks unlock dates (seconds). |
| **DiscordWebhookURL** | *(empty)* | Optional Discord webhook URL for unlock/countdown messages. |

### Per-boss (e.g. Eikthyr, Elder, Bonemass, …)

| Setting | Description |
|--------|-------------|
| **DisplayName** | Name used in chat/Discord messages. |
| **Enabled** | Turn scheduled unlock on/off for this boss. |
| **UnlockAt** | Date and time in **Philippines (GMT+8)**. Format: `yyyy-MM-dd` or `yyyy-MM-dd HH:mm` or `yyyy-MM-dd HH:mm:ss`. |

**Example:** `UnlockAt = 2027-01-01 18:00` → boss unlocks on 1 Jan 2027 at 18:00 Philippines time.

---

## How it works

- Only the **server** runs the unlock checks. When the current time (UTC) reaches the configured unlock time, the mod sets the game’s global key for that boss (e.g. `defeated_eikthyr`), so the boss is considered defeated for progression.
- Until then, **Harmony** patches block:
  - Interacting with the boss altar to summon the boss.
  - Using the required offering item on the altar.
- If a player tries to use an altar too early, they see a message with the unlock date/time in Philippines (GMT+8).

---

## Support

- **Bugs / feature ideas:** [GitHub Issues](https://github.com/Arthistic04/Arthistic-ScheduleBossFight)
- **Source:** [GitHub – Arthistic04/Arthistic-ScheduleBossFight](https://github.com/Arthistic04/Arthistic-ScheduleBossFight)

---

## Changelog
| Setting | Description |
|---------|-------------|
| **1.1.2** | UTC Offset set to configurable |
| **1.1.1** | Removed global key (defeated_eikthyr etc.) after the configured date, Error on me |
| **1.1.0** | Release for production |