namespace TextFileProcessor.Models;

public sealed class IspmanagerSettings
{
    public string PanelUrl { get; set; } =
        "https://185.115.33.18:1500/ispmgr";

    public string Login { get; set; } = "root";

    public string EncryptedPassword { get; set; } = string.Empty;

    public string Owner { get; set; } = "www-root";

    public string PhpVersion { get; set; } = "8.3.8";

    public bool IgnoreCertificateErrors { get; set; } = true;

    public bool ShowBrowser { get; set; } = true;
}
