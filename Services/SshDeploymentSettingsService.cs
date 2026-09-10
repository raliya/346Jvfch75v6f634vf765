using System.Text;
using System.Text.Json;
using TextFileProcessor.Models;
using TextFileProcessor.Security;

namespace TextFileProcessor.Services;

public sealed class SshDeploymentSettingsService
{
    private readonly SshSecretProtector _protector = new();

    public string SettingsPath { get; }

    public SshDeploymentSettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "TextFileProcessor");

        Directory.CreateDirectory(directory);

        SettingsPath = Path.Combine(
            directory,
            "ssh-deployment-settings.json");
    }

    public SshDeploymentSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new SshDeploymentSettings();
        }

        try
        {
            var json = File.ReadAllText(
                SettingsPath,
                Encoding.UTF8);

            return JsonSerializer
                       .Deserialize<SshDeploymentSettings>(json)
                   ?? new SshDeploymentSettings();
        }
        catch
        {
            return new SshDeploymentSettings();
        }
    }

    public void Save(
        SshDeploymentSettings settings,
        string plainPassword)
    {
        var savedSettings = new SshDeploymentSettings
        {
            Host = settings.Host,
            Port = settings.Port,
            Username = settings.Username,
            HostKeySha256 = settings.HostKeySha256,
            RemoteSitesRoot = settings.RemoteSitesRoot,
            Owner = settings.Owner,
            Group = settings.Group,

            EncryptedPassword =
                string.IsNullOrEmpty(plainPassword)
                    ? settings.EncryptedPassword
                    : _protector.Protect(plainPassword)
        };

        var json = JsonSerializer.Serialize(
            savedSettings,
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
            savedSettings.EncryptedPassword;
    }

    public string GetPassword(
        SshDeploymentSettings settings,
        string enteredPassword)
    {
        if (!string.IsNullOrEmpty(enteredPassword))
        {
            return enteredPassword;
        }

        return _protector.Unprotect(
            settings.EncryptedPassword);
    }
}
