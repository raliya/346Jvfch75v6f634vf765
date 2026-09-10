using System.Text;
using System.Text.Json;
using TextFileProcessor.Models;
using TextFileProcessor.Security;

namespace TextFileProcessor.Services;

public sealed class SftpSettingsService
{
    private readonly DpapiSecretProtector _protector =
        new();

    public string SettingsPath { get; }

    public SftpSettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "TextFileProcessor");

        Directory.CreateDirectory(directory);

        SettingsPath = Path.Combine(
            directory,
            "sftp-settings.json");
    }

    public SftpSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new SftpSettings();
        }

        try
        {
            var json = File.ReadAllText(
                SettingsPath,
                Encoding.UTF8);

            return JsonSerializer.Deserialize<SftpSettings>(
                json)
                ?? new SftpSettings();
        }
        catch
        {
            return new SftpSettings();
        }
    }

    public void Save(
        SftpSettings settings,
        string plainPassword)
    {
        var settingsToSave = new SftpSettings
        {
            Host = settings.Host,
            Port = settings.Port,
            Login = settings.Login,
            HostKeyFingerprint =
                settings.HostKeyFingerprint,
            RemoteWebRoot = settings.RemoteWebRoot,
            Owner = settings.Owner,
            Group = settings.Group,

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
        SftpSettings settings,
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