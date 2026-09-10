using System.Security.Cryptography;
using System.Text;

namespace TextFileProcessor.Security;

public sealed class SshSecretProtector
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes(
            "TextFileProcessor.SSH.Password.v1");

    public string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var plainBytes = Encoding.UTF8.GetBytes(value);
        byte[]? protectedBytes = null;

        try
        {
            protectedBytes = ProtectedData.Protect(
                plainBytes,
                Entropy,
                DataProtectionScope.CurrentUser);

            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(
                    protectedBytes);
            }
        }
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return string.Empty;
        }

        byte[] encryptedBytes;

        try
        {
            encryptedBytes =
                Convert.FromBase64String(protectedValue);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Сохранённый SSH-пароль повреждён.",
                exception);
        }

        byte[]? plainBytes = null;

        try
        {
            plainBytes = ProtectedData.Unprotect(
                encryptedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                "Не удалось расшифровать SSH-пароль. " +
                "Он доступен только текущей учётной записи Windows.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedBytes);

            if (plainBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }
}
