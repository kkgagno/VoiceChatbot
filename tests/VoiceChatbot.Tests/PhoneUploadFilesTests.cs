using System;
using System.IO;
using VoiceChatbot;
using Xunit;

public class PhoneUploadFilesTests
{
    [Theory]
    [InlineData("photo.JPG", "photo.JPG")]
    [InlineData("../../evil.png", "evil.png")]
    [InlineData("..\\..\\Windows\\evil.exe", "evil.exe")]
    [InlineData("C:\\Users\\me\\notes.txt", "notes.txt")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("a.png:hidden", "a.png_hidden")]
    [InlineData("report\u202Efdp.exe", "reportfdp.exe")]
    [InlineData("line\nbreak\u0000.txt", "linebreak.txt")]
    [InlineData("  trailing dots...  ", "trailing dots")]
    [InlineData("meeting \U0001F4DD.wav", "meeting \U0001F4DD.wav")]
    [InlineData("..", PhoneUploadFiles.DefaultDisplayName)]
    [InlineData("folder/", PhoneUploadFiles.DefaultDisplayName)]
    [InlineData("", PhoneUploadFiles.DefaultDisplayName)]
    [InlineData(null, PhoneUploadFiles.DefaultDisplayName)]
    public void SafeDisplayName(string? input, string expected) =>
        Assert.Equal(expected, PhoneUploadFiles.SafeDisplayName(input));

    [Fact]
    public void LongDisplayNamesKeepTheirExtension()
    {
        var name = PhoneUploadFiles.SafeDisplayName(new string('x', 500) + ".pdf");

        Assert.Equal(PhoneUploadFiles.MaxDisplayNameLength, name.Length);
        Assert.EndsWith("x.pdf", name);
    }

    [Theory]
    [InlineData("photo.JPG", ".jpg")]
    [InlineData("doc.v2.docx", ".docx")]
    [InlineData("noext", "")]
    [InlineData("dot.", "")]
    [InlineData("a.png:stream", "")]
    [InlineData("a.p g", "")]
    [InlineData("a.toolongextension", "")]
    [InlineData("a.exe/..", "")]
    [InlineData("a.\u00e9t\u00e9", "")]
    [InlineData(null, "")]
    public void SafeExtension(string? input, string expected) =>
        Assert.Equal(expected, PhoneUploadFiles.SafeExtension(input));

    [Theory]
    [InlineData("../../../evil.png", ".png")]
    [InlineData("..\\..\\evil.exe", ".exe")]
    [InlineData("/abs/path/x.wav", ".wav")]
    [InlineData("a.png:stream", "")]
    [InlineData("x.png/../../../../tmp/y", "")]
    [InlineData(null, "")]
    public void CreateTempPathStaysInsideTheFolder(string? upload, string expectedExtension)
    {
        var folder = Path.Combine(Path.GetTempPath(), "VoiceChatbotTests", "phone-uploads");

        var path = PhoneUploadFiles.CreateTempPath(folder, upload);

        Assert.True(PhoneUploadFiles.IsDirectlyInside(folder, path));
        Assert.Equal(Path.GetFullPath(folder), Path.GetDirectoryName(path));
        Assert.Equal(expectedExtension, Path.GetExtension(path));
        Assert.Equal(32 + expectedExtension.Length, Path.GetFileName(path).Length);
    }

    [Fact]
    public void CreateTempPathIsUniqueEachTime()
    {
        var folder = Path.Combine(Path.GetTempPath(), "VoiceChatbotTests");

        Assert.NotEqual(PhoneUploadFiles.CreateTempPath(folder, "a.png"), PhoneUploadFiles.CreateTempPath(folder, "a.png"));
    }

    [Fact]
    public void IsDirectlyInsideRejectsOtherFolders()
    {
        var folder = Path.Combine(Path.GetTempPath(), "VoiceChatbotTests", "uploads");

        Assert.True(PhoneUploadFiles.IsDirectlyInside(folder, Path.Combine(folder, "a.png")));
        Assert.True(PhoneUploadFiles.IsDirectlyInside(folder + Path.DirectorySeparatorChar, Path.Combine(folder, "a.png")));
        Assert.False(PhoneUploadFiles.IsDirectlyInside(folder, Path.Combine(folder, "..", "a.png")));
        Assert.False(PhoneUploadFiles.IsDirectlyInside(folder, Path.Combine(folder, "sub", "a.png")));
        Assert.False(PhoneUploadFiles.IsDirectlyInside(folder, folder + "-other" + Path.DirectorySeparatorChar + "a.png"));
        Assert.False(PhoneUploadFiles.IsDirectlyInside(folder, folder));
    }
}
