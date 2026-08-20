using System;
using System.IO;
using System.Text.Json;
using UnoBrowser.UnoApp.Models;

namespace UnoBrowser.UnoApp.Services;

/// <summary>
/// 应用设置服务 — JSON 持久化到软件目录 config/settings.json。
/// </summary>
public class SettingsService : ISettingsService
{
    private static readonly string SettingsDir = AppPaths.Config;
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        Directory.CreateDirectory(SettingsDir);
        MigrateLegacySettings();
    }

    /// <summary>
    /// 迁移旧版本路径下的 settings.json（%LocalAppData%/SCAssistant/settings.json），
    /// 避免升级后用户设置丢失。
    /// </summary>
    private static void MigrateLegacySettings()
    {
        try
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SCAssistant", "settings.json"); // 旧版 SCAssistant 路径，保留迁移兼容
            if (File.Exists(legacyPath) && !File.Exists(SettingsPath))
            {
                Directory.CreateDirectory(SettingsDir);
                File.Move(legacyPath, SettingsPath);
                LogHelper.Info($"[设置] 已迁移旧配置文件: {legacyPath} -> {SettingsPath}");
            }
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[设置] 旧配置迁移失败: {ex.Message}");
        }
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    Settings = loaded;
                    LogHelper.Info($"[设置] 已加载: UA平台={Settings.UserAgentPlatform}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            LogHelper.Error("[设置] 加载失败，使用默认设置", ex);
        }

        Settings = new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
            LogHelper.Info($"[设置] 已保存: UA平台={Settings.UserAgentPlatform}");
        }
        catch (Exception ex)
        {
            LogHelper.Error("[设置] 保存失败", ex);
        }
    }
}
