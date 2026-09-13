using System.Text;
using System.Text.Json;
using TextFileProcessor.Models;
using TextFileProcessor.Security;

namespace TextFileProcessor.Services;

public sealed class IspmanagerSettingsService
{
    private readonly DpapiSecretProtector _protector = new();

    public string SettingsPath { get; }

    public IspmanagerSettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "TextFileProcessor");

        Directory.CreateDirectory(directory);

        SettingsPath = Path.Combine(
            directory,
            "ispmanager-settings.json");
    }

    public IspmanagerSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new IspmanagerSettings();
        }

        try
        {
            var json = File.ReadAllText(
                SettingsPath,
                Encoding.UTF8);

            return JsonSerializer.Deserialize<IspmanagerSettings>(
                       json)
                   ?? new IspmanagerSettings();
        }
        catch
        {
            return new IspmanagerSettings();
        }
    }

    public void Save(
        IspmanagerSettings settings,
        string plainPassword)
    {
        var settingsToSave = new IspmanagerSettings
        {
            PanelUrl = settings.PanelUrl,
            Login = settings.Login,
            Owner = settings.Owner,
            PhpVersion = settings.PhpVersion,
            IgnoreCertificateErrors =
                settings.IgnoreCertificateErrors,
            ShowBrowser = settings.ShowBrowser,
            EncryptedPassword =
                string.IsNullOrEmpty(plainPassword)
                    ? settings.EncryptedPassword
                    : _protector.Protect(plainPassword)
        };

        var json = JsonSerializer.Serialize(
            settingsToSave,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        var temporaryPath =
            SettingsPath + "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        File.WriteAllText(
            temporaryPath,
            json,
            new UTF8Encoding(false));

        File.Move(
            temporaryPath,
            SettingsPath,
            true);

        settings.EncryptedPassword =
            settingsToSave.EncryptedPassword;
    }

    public string GetPassword(
        IspmanagerSettings settings,
        string passwordEnteredInInterface)
    {
        if (!string.IsNullOrEmpty(
                passwordEnteredInInterface))
        {
            return passwordEnteredInInterface;
        }

        return _protector.Unprotect(
            settings.EncryptedPassword);
    }
}

