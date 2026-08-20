using UnoBrowser.UnoApp.Models;

namespace UnoBrowser.UnoApp.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Save();
    void Load();
}
