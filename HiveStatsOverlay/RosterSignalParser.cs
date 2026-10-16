namespace HiveStatsOverlay;

internal static class RosterSignalParser
{
    public static bool IsRemovalSignalForPlayer(string message, string username)
    {
        if (message.Length == 0 || username.Length == 0)
            return false;

        return ContainsBounded(message, $"{username} was eliminated") ||
               ContainsBounded(message, $"{username} has been eliminated") ||
               ContainsBounded(message, $"eliminated {username}") ||
               ContainsBounded(message, $"{username} left") ||
               ContainsBounded(message, $"{username} has left") ||
               ContainsBounded(message, $"{username} disconnected");
    }

    private static bool ContainsBounded(string value, string phrase)
    {
        int start = 0;
        while ((start = value.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = start + phrase.Length;
            bool validStart = start == 0 || !IsUsernameCharacter(value[start - 1]);
            bool validEnd = end == value.Length || !IsUsernameCharacter(value[end]);
            if (validStart && validEnd)
                return true;
            start++;
        }
        return false;
    }

    private static bool IsUsernameCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value == '_';
}
