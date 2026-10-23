namespace TextTools;

public static class TextTools
{
    /// <summary>Returns the characters of <paramref name="value"/> in reverse order.</summary>
    public static string Reverse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var characters = value.ToCharArray();
        Array.Reverse(characters);
        return new string(characters);
    }
}
