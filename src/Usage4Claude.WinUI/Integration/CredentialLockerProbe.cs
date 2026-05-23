using Windows.Security.Credentials;

namespace Usage4Claude.WinUI.Integration;

internal sealed class CredentialLockerProbe
{
    private const string ClaudeResource = "Usage4Claude.Claude.SessionKey";
    private const string ClaudeUserName = "claude-session-key";

    private readonly PasswordVault _vault = new();

    public void SaveClaudeSessionKey(string sessionKey)
    {
        DeleteClaudeSessionKey();
        _vault.Add(new PasswordCredential(ClaudeResource, ClaudeUserName, sessionKey));
    }

    public string? LoadClaudeSessionKey()
    {
        var credential = FindClaudeCredential();
        if (credential is null)
        {
            return null;
        }

        credential.RetrievePassword();
        return credential.Password;
    }

    public bool DeleteClaudeSessionKey()
    {
        var credential = FindClaudeCredential();
        if (credential is null)
        {
            return false;
        }

        _vault.Remove(credential);
        return true;
    }

    private PasswordCredential? FindClaudeCredential()
    {
        try
        {
            return _vault
                .FindAllByResource(ClaudeResource)
                .FirstOrDefault(credential => credential.UserName == ClaudeUserName);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
