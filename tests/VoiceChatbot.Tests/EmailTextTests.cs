using System.Text;
using VoiceChatbot;
using Xunit;

public class EmailTextTests
{
    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));

    [Fact]
    public void PlainEmailGivesHeadersThenBody()
    {
        var eml = Bytes("""
            Return-Path: <x@example.com>
            Received: from mail.example.com
            From: "Jane Doe" <jane@example.com>
            To: owner@example.com
            Subject: Roof inspection
             scheduled
            Date: Tue, 3 Sep 2024 10:15:00 -0400
            Content-Type: text/plain; charset=utf-8

            Hi,

            The roofer comes Friday.
            """);

        Assert.Equal(
            "From: \"Jane Doe\" <jane@example.com>\n" +
            "To: owner@example.com\n" +
            "Date: Tue, 3 Sep 2024 10:15:00 -0400\n" +
            "Subject: Roof inspection scheduled\n\n" +
            "Hi,\n\nThe roofer comes Friday.",
            EmailText.Extract(eml));
    }

    [Fact]
    public void MultipartPrefersPlainTextDecodesQuotedPrintableAndListsAttachments()
    {
        var eml = Bytes("""
            From: =?UTF-8?B?Sm9zw6k=?= <jose@example.com>
            Subject: =?utf-8?Q?HOA_fee_=E2=82=AC350?= =?utf-8?Q?_due?=
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="outer"

            This is a multi-part message in MIME format.
            --outer
            Content-Type: multipart/alternative; boundary=inner

            --inner
            Content-Type: text/plain; charset="iso-8859-1"
            Content-Transfer-Encoding: quoted-printable

            The fee is due on the 1st. Caf=E9 meeting at 7=
             pm.
            --inner
            Content-Type: text/html; charset=utf-8

            <p>HTML version</p>
            --inner--
            --outer
            Content-Type: application/pdf; name="statement.pdf"
            Content-Disposition: attachment; filename="statement.pdf"
            Content-Transfer-Encoding: base64

            JVBERi0xLjQK
            --outer
            Content-Type: text/plain; name="notes.txt"
            Content-Disposition: attachment; filename*=UTF-8''r%C3%A9sum%C3%A9.txt

            attached text is not read
            --outer--
            """);

        var text = EmailText.Extract(eml);

        Assert.Equal(
            "From: José <jose@example.com>\n" +
            "Subject: HOA fee €350 due\n" +
            "Attachments: statement.pdf, résumé.txt\n\n" +
            "The fee is due on the 1st. Café meeting at 7 pm.",
            text);
    }

    [Fact]
    public void HtmlOnlyAndBase64PartsAreConvertedToText()
    {
        var html = Convert.ToBase64String(Encoding.UTF8.GetBytes("<html><body><p>Closing on <b>June&nbsp;5</b>.</p><p>Bring ID – thanks</p></body></html>"));
        var eml = Bytes($"""
            From: agent@example.com
            Subject: Closing
            Content-Type: multipart/related; boundary=rel

            --rel
            Content-Type: text/html; charset=UTF-8
            Content-Transfer-Encoding: base64

            {html[..20]}
            {html[20..]}
            --rel
            Content-Type: image/png
            Content-Disposition: inline; filename="logo.png"
            Content-ID: <logo>

            iVBORw0KGgo=
            --rel--
            """);

        Assert.Equal(
            "From: agent@example.com\nSubject: Closing\n\nClosing on June 5.\n\nBring ID – thanks",
            EmailText.Extract(eml));
    }

    [Fact]
    public void ForwardedMessagesAndMboxLinesAreHandled()
    {
        var eml = Bytes("""
            From jane@example.com Tue Sep  3 10:15:00 2024
            From: jane@example.com
            Subject: Fwd: Survey
            Content-Type: multipart/mixed; boundary=b

            --b
            Content-Type: text/plain

            See below.
            --b
            Content-Type: message/rfc822

            From: surveyor@example.com
            Subject: Survey results

            Lot lines are fine.
            --b--
            """);

        Assert.Equal(
            "From: jane@example.com\nSubject: Fwd: Survey\n\nSee below.\n\n---------- Forwarded message ----------\nFrom: surveyor@example.com\nSubject: Survey results\n\nLot lines are fine.",
            EmailText.Extract(eml));
    }

    [Fact]
    public void RawUtf8HeadersAndCutOffMessagesStillRead()
    {
        var eml = Encoding.UTF8.GetBytes("Subject: Réunion\r\nContent-Type: multipart/mixed; boundary=x\r\n\r\n--x\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nÀ bientôt");

        Assert.Equal("Subject: Réunion\n\nÀ bientôt", EmailText.Extract(eml));
        Assert.Equal("", EmailText.Extract(Array.Empty<byte>()));
    }

    [Fact]
    public void DecodersHandleSoftBreaksPaddingAndUnderscores()
    {
        Assert.Equal("a=b c", Encoding.ASCII.GetString(EmailText.DecodeQuotedPrintable("a=3Db=\r\n c", header: false)));
        Assert.Equal("a b", Encoding.ASCII.GetString(EmailText.DecodeQuotedPrintable("a_b", header: true)));
        Assert.Equal("hi", Encoding.ASCII.GetString(EmailText.DecodeBase64("aGk")));
        Assert.Equal("hello", Encoding.ASCII.GetString(EmailText.DecodeBase64("aGVs\r\nbG8=")));
        Assert.Equal("Café Ünïcode", EmailText.DecodeHeader("=?ISO-8859-1?Q?Caf=E9_?= =?utf-8?B?w5xuw69jb2Rl?="));
        Assert.Equal("plain", EmailText.DecodeHeader("plain"));
    }

    [Fact]
    public void EmailSniffRecognizesHeaders()
    {
        Assert.True(EmailText.LooksLikeEmail("From: a@b.c\r\nTo: d@e.f"));
        Assert.True(EmailText.LooksLikeEmail("MIME-Version: 1.0\r\n"));
        Assert.False(EmailText.LooksLikeEmail("Hello there"));
    }
}
