namespace RotaryEmailForwarding.FunctionApp.Email;

public static class EmailAddressUtility
{
    public static bool IsUsable(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var trimmed = email.Trim();
        return trimmed.Contains('@', StringComparison.Ordinal)
            && trimmed.IndexOf('@', StringComparison.Ordinal) > 0
            && trimmed.LastIndexOf('@') < trimmed.Length - 1
            && !trimmed.Contains(' ', StringComparison.Ordinal);
    }
}
