using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.IO;
using UnityEngine;
using System.Globalization;

[BepInPlugin("arthistic_scheduledbossfight", "Scheduled Boss Fight", "1.1.0")]
[BepInProcess("valheim.exe")]
[BepInProcess("valheim_server.exe")]
public class ScheduleBossFight : BaseUnityPlugin
{

    private ConfigSync configSync;
    private ConfigEntry<bool> lockConfig;
    private ConfigEntry<int> checkInterval;
    private ConfigEntry<string> discordWebhook;
    private Harmony harmony;
    private FileSystemWatcher configWatcher;
    private bool configDirty;
    private float lastConfigReloadTime;

    private class BossEntry
    {
        public string Section;
        public string AltarPrefab;
        public string Key;
        public string TrophyName;

        public ConfigEntry<string> DisplayName;
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<string> UnlockAt;

        public bool BroadcastedTomorrow;
        public bool BroadcastedCountdown;
        public bool BroadcastedUnlocked;
    }

    private readonly Dictionary<string, BossEntry> bosses = new Dictionary<string, BossEntry>();

    // Philippines time is fixed UTC+8 (no DST)
    private static readonly TimeSpan PhilippinesUtcOffset = TimeSpan.FromHours(8);

    private static bool TryGetUnlockAtUtc(BossEntry boss, out DateTimeOffset unlockAtUtc, out string error)
    {
        // Accept:
        // - "yyyy-MM-dd" (assumed 00:00 PH time)
        // - "yyyy-MM-dd HH:mm"
        // - "yyyy-MM-dd HH:mm:ss"
        var raw = (boss.UnlockAt?.Value ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw))
        {
            unlockAtUtc = default;
            error = "empty value";
            return false;
        }

        string[] formats = { "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" };
        if (!DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localPhilippines))
        {
            unlockAtUtc = default;
            error = $"invalid format \"{raw}\" (expected yyyy-MM-dd[ HH:mm[:ss]] in PH time)";
            return false;
        }

        // Treat parsed DateTime as Philippines local time (Unspecified kind).
        var unlockAtPhilippines = new DateTimeOffset(DateTime.SpecifyKind(localPhilippines, DateTimeKind.Unspecified), PhilippinesUtcOffset);
        unlockAtUtc = unlockAtPhilippines.ToUniversalTime();
        error = string.Empty;
        return true;
    }

    private static string FormatPhilippines(DateTimeOffset utc)
    {
        var ph = utc.ToOffset(PhilippinesUtcOffset);
        return ph.ToString("MMMM dd, yyyy HH:mm", CultureInfo.InvariantCulture) + " (PH / GMT+8)";
    }

    private void Awake()
    {
        configSync = new ConfigSync("arthistic_scheduledbossfight")
        {
            DisplayName = "Scheduled Boss Fight",
            CurrentVersion = "1.1.0",
            MinimumRequiredVersion = "1.1.0",
            ModRequired = true
        };

        lockConfig = config("1 - General", "Lock Configuration", false,
            "If on, the configuration is locked and can be changed by server admins only. [Synced with Server]");
        checkInterval = config("1 - General", "CheckIntervalSeconds", 300, "How often to check unlock dates (seconds).");
        discordWebhook = config("1 - General", "DiscordWebhookURL", "", "Optional Discord webhook URL.");

        configSync.AddLockingConfigEntry(lockConfig);
        configSync.AddConfigEntry(checkInterval);
        configSync.AddConfigEntry(discordWebhook);

        // Register bosses (first argument = altar prefab name)
        // Eikthyr altar prefab is "offeraltar_deer"
        RegisterBoss(2, "Eikthyr", "offeraltar_deer", "defeated_eikthyr", "TrophyDeer");
        RegisterBoss(3, "Elder", "fire_button", "defeated_gdking", "AncientSeed");
        RegisterBoss(4, "Bonemass", "offeraltar", "defeated_bonemass", "WitheredBone");
        RegisterBoss(5, "Moder", "offeraltar_dragon", "defeated_dragon", "DragonEgg");
        RegisterBoss(6, "Yagluth", "offeraltar_goblinking", "defeated_goblinking", "GoblinTotem");
        RegisterBoss(7, "Queen", "offeraltar_queen", "defeated_queen", "QueenTrophy");
        RegisterBoss(8, "Fader", "offeraltar_fader", "defeated_fader", "Bell");


        foreach (BossEntry boss in bosses.Values)
        {
            configSync.AddConfigEntry(boss.DisplayName);
            configSync.AddConfigEntry(boss.Enabled);
            configSync.AddConfigEntry(boss.UnlockAt);
        }

        StartCoroutine(DelayedCheck());

        // Harmony patch
        harmony = new Harmony("arthistic_scheduledbossfight");
        harmony.PatchAll();

        // Watch config file for external edits so changes apply without restart
        SetupConfigWatcher();
    }

    /*private void OnDestroy()
    {
        harmony?.UnpatchAll("arthistic_scheduledbossfight");
        if (configWatcher != null)
        {
            configWatcher.EnableRaisingEvents = false;
            configWatcher.Dispose();
            configWatcher = null;
        }
    }*/

    private void SetupConfigWatcher()
    {
        try
        {
            var path = Config.ConfigFilePath;
            var dir = Path.GetDirectoryName(path);
            var file = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
                return;

            configWatcher = new FileSystemWatcher(dir, file);
            configWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
            configWatcher.Changed += OnConfigFileChanged;
            configWatcher.Created += OnConfigFileChanged;
            configWatcher.Renamed += OnConfigFileChanged;
            configWatcher.EnableRaisingEvents = true;

            Logger.LogInfo($"Config watcher set up on {path}");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Failed to set up config watcher: " + ex.Message);
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Mark dirty; actual reload happens on Unity main thread in Update
        configDirty = true;
    }

    private void Update()
    {
        if (!configDirty)
            return;

        // Debounce rapid change events
        if (Time.realtimeSinceStartup - lastConfigReloadTime < 0.5f)
            return;

        configDirty = false;
        lastConfigReloadTime = Time.realtimeSinceStartup;

        if (configSync != null && !configSync.IsSourceOfTruth)
            return;

        try
        {
            Config.Reload();
            Logger.LogInfo("ScheduleBossFight config reloaded from disk.");

            // Reset per-boss runtime flags so new schedule is applied cleanly
            foreach (var boss in bosses.Values)
            {
                boss.BroadcastedCountdown = false;
                boss.BroadcastedTomorrow = false;
                boss.BroadcastedUnlocked = false;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Failed to reload config: " + ex.Message);
        }
    }

    private void RegisterBoss(
    int order,
    string sectionName,
    string altarPrefab,
    string globalKey,
    string trophyName)
    {
        string section = $"{order} - Boss: {sectionName}";

        var displayName = config(
            section,
            "DisplayName",
            sectionName,
            $"Name used in messages for {sectionName}"
        );

        var enabled = config(
            section,
            "Enabled",
            true,
            $"Enable scheduled unlock for {sectionName}"
        );

        var unlockAt = config(
            section,
            "UnlockAt",
            "2027-01-01 18:00",
            "Philippines time (GMT+8). Format: yyyy-MM-dd HH:mm[:ss]"
        );

        bosses[altarPrefab] = new BossEntry
        {
            Section = section,
            AltarPrefab = altarPrefab,
            Key = globalKey,
            TrophyName = trophyName,
            DisplayName = displayName,
            Enabled = enabled,
            UnlockAt = unlockAt,
            BroadcastedCountdown = false,
            BroadcastedTomorrow = false,
            BroadcastedUnlocked = false
        };
    }



    private IEnumerator DelayedCheck()
    {
        while (ZNet.instance == null || !ZNet.instance.IsServer())
            yield return null;

        InvokeRepeating(nameof(CheckBossUnlocks), 0f, checkInterval.Value);
    }

    private void CheckBossUnlocks()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
            return;

        var nowUtc = DateTimeOffset.UtcNow;

        foreach (var kvp in bosses)
        {
            var boss = kvp.Value;
            if (!boss.Enabled.Value) continue;

            if (!TryGetUnlockAtUtc(boss, out var unlockAtUtc, out var error))
            {
                Logger.LogWarning($"[ScheduleBossFight] Invalid UnlockAt for {kvp.Key}: {error}");
                continue;
            }

            // Skip if boss was already defeated (game set the global key); no need to broadcast
            if (ZoneSystem.instance.GetGlobalKey(boss.Key))
            {
                boss.BroadcastedUnlocked = true;
                continue;
            }

            var timeUntil = unlockAtUtc - nowUtc;

            // Broadcast countdown if more than 1 day away
            if (!boss.BroadcastedCountdown && timeUntil.TotalDays > 1)
            {
                SendGlobalMessage($"[ScheduleBossFight] {boss.DisplayName.Value} unlocks in {timeUntil.Days}d {timeUntil.Hours}h {timeUntil.Minutes}m.");
                SendDiscordMessage($"**{boss.DisplayName.Value}** unlocks in {timeUntil.Days}d {timeUntil.Hours}h {timeUntil.Minutes}m.");
                boss.BroadcastedCountdown = true;
            }

            // Broadcast 1 day before unlock
            if (!boss.BroadcastedTomorrow && nowUtc.ToOffset(PhilippinesUtcOffset).Date == unlockAtUtc.ToOffset(PhilippinesUtcOffset).AddDays(-1).Date)
            {
                SendGlobalMessage($"[ScheduleBossFight] {boss.DisplayName.Value} unlocks tomorrow!");
                SendDiscordMessage($"**{boss.DisplayName.Value}** unlocks tomorrow!");
                boss.BroadcastedTomorrow = true;
            }

            // On configured date: allow summoning (do not set global key — game sets it when boss is defeated)
            if (!boss.BroadcastedUnlocked && nowUtc >= unlockAtUtc)
            {
                Logger.LogInfo($"[ScheduleBossFight] {boss.DisplayName.Value} summoning is now allowed (date reached).");
                SendGlobalMessage($"[ScheduleBossFight] {boss.DisplayName.Value} is now available to summon!");
                SendDiscordMessage($"**{boss.DisplayName.Value}** is now available to summon!");
                boss.BroadcastedUnlocked = true;
            }
        }
    }

    private void SendGlobalMessage(string message)
    {
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (peer.m_rpc != null)
                peer.m_rpc.Invoke("ChatMessage", new object[] { message, 0 });
        }
        Logger.LogInfo("[CHAT] " + message);
    }

    private void SendDiscordMessage(string message)
    {
        if (string.IsNullOrEmpty(discordWebhook.Value)) return;

        try
        {
            using (var client = new WebClient())
            {
                client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
                string payload = $"{{\"content\": \"{message}\"}}";
                client.UploadString(discordWebhook.Value, payload);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Failed to send Discord message: " + ex.Message);
        }
    }

    // ---------------- Harmony patch ----------------
    [HarmonyPatch(typeof(OfferingBowl), nameof(OfferingBowl.Interact))]
    public static class PreventEarlyBossSummon
    {
        static bool Prefix(OfferingBowl __instance, Humanoid user, bool hold, bool alt)
        {
            // Run on both client and server; only ignore hold interactions
            if (hold || user == null)
                return true;

            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject
                .GetComponent<ScheduleBossFight>();
            if (plugin == null)
                return true;

            string altarPrefab = __instance.gameObject.name.Replace("(Clone)", "");
            plugin.Logger.LogInfo($"OfferingBowl.Interact on '{altarPrefab}'");
            var nowUtc = DateTimeOffset.UtcNow;

            foreach (var boss in plugin.bosses)
            {
                if (!boss.Value.Enabled.Value)
                    continue;

                if (!TryGetUnlockAtUtc(boss.Value, out var unlockAtUtc, out _))
                    continue;

                // Match altar prefab name
                if (altarPrefab.IndexOf(boss.Key, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // Already unlocked → allow summon
                if (ZoneSystem.instance.GetGlobalKey(boss.Value.Key))
                    return true;

                // Block early summon
                if (nowUtc < unlockAtUtc)
                {
                    user.Message(
                        MessageHud.MessageType.Center,
                        $"{boss.Value.DisplayName.Value} cannot be summoned until {FormatPhilippines(unlockAtUtc)}"
                    );
                    return false;
                }
            }

            return true;
        }
    }

    // Block using boss offering items via hotbar (UseItem on the altar)
    [HarmonyPatch(typeof(OfferingBowl), nameof(OfferingBowl.UseItem))]
    public static class PreventEarlyBossUseItem
    {
        // Signature for OfferingBowl.UseItem is: bool UseItem(Humanoid user, ItemDrop.ItemData item)
        static bool Prefix(OfferingBowl __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (user == null || item == null)
                return true;

            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject
                .GetComponent<ScheduleBossFight>();
            if (plugin == null)
                return true;

            string altarPrefab = __instance.gameObject.name.Replace("(Clone)", "");
            string itemPrefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty;
            plugin.Logger.LogInfo($"OfferingBowl.UseItem on '{altarPrefab}' with itemPrefab='{itemPrefab}', sharedName='{item.m_shared.m_name}'");

            var nowUtc = DateTimeOffset.UtcNow;

            foreach (var boss in plugin.bosses)
            {


                if (!boss.Value.Enabled.Value)
                    continue;

                if (!TryGetUnlockAtUtc(boss.Value, out var unlockAtUtc, out _))
                    continue;

                // Match altar prefab
                if (altarPrefab.IndexOf(boss.Key, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // Match required offering item (by prefab or shared name)
                if (!string.Equals(itemPrefab, boss.Value.TrophyName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.m_shared.m_name, boss.Value.TrophyName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Already globally unlocked → allow
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(boss.Value.Key))
                    return true;

                // Before unlock date → block item use and boss spawn
                if (nowUtc < unlockAtUtc)
                {
                    user.Message(
                        MessageHud.MessageType.Center,
                        $"{boss.Value.DisplayName.Value} cannot be summoned until {FormatPhilippines(unlockAtUtc)}"
                    );

                    // Pretend success so vanilla Interact logic
                    // does not show "You can't use Deer Trophy on Mystical Altar".
                    __result = true;
                    return false; // skip original UseItem implementation
                }
            }

            return true;
        }
    }
    ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

        SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

        return configEntry;
    }
    ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);
}