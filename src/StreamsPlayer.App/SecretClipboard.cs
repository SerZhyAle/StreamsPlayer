using System.IO;
using System.Windows;

namespace StreamsPlayer.App;

/// <summary>
/// Puts a secret on the clipboard the way Windows asks a secret to be treated: out of the clipboard history, out of the
/// cloud clipboard and out of clipboard monitors that honour the format. The person still pastes it where they choose;
/// this only stops Windows from keeping a copy of it for them.
/// </summary>
internal static class SecretClipboard
{
    internal static void SetText(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(1)));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Clipboard.SetDataObject(data, copy: true);
    }
}
