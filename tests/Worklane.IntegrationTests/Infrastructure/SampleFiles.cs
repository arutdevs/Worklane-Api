namespace Worklane.IntegrationTests.Infrastructure;

internal static class SampleFiles
{
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xD9];
    public static readonly byte[] Pdf = "%PDF-1.4\n"u8.ToArray();
    public static readonly byte[] Text = "hello"u8.ToArray();
    public static readonly byte[] Html = "<html>not-an-image</html>"u8.ToArray();
}
