using QRCoder;

namespace GomokuClient.Web.Security;

public static class QrCodeRenderer
{
    public static string ToPngDataUri(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 8);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }
}
