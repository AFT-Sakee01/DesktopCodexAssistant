using System.Text;

internal static class SharedEncoding
{
    internal static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
}
