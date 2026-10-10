using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// Splits a finished reply's speech text into pieces for the built-in Kokoro, so the voice starts after the
/// first piece is made instead of after the whole reply: a short first piece (usually the first sentence, at
/// least <see cref="FirstPieceMinLength"/> characters), then longer pieces that are made while the one before
/// plays. Pieces end at sentence ends. Kokoro garbles very short clips ("Sure!"), not whole sentences, so no
/// piece is shorter than <see cref="FirstPieceMinLength"/> unless the whole text is: a short last part joins
/// the piece before it.
/// </summary>
public static class SpeechPieces
{
    /// <summary>
    /// The first piece, and the shortest any piece can be: usually the first sentence, or two short ones.
    /// A typical reply of three sentences is then spoken as its first sentence and the rest.
    /// </summary>
    public const int FirstPieceMinLength = 60;

    /// <summary>
    /// The second piece is made while the short first one plays, so it stays moderate; a longer one could
    /// leave a pause between them on a slow processor.
    /// </summary>
    public const int SecondPieceMinLength = 220;

    /// <summary>Every later piece: a few sentences, for a natural flow.</summary>
    public const int LaterPieceMinLength = 350;

    /// <summary>The pieces of <paramref name="text"/> (plain speech text) in order; empty for blank text.</summary>
    public static List<string> Split(string? text)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in SentenceChunker.Split(text))
        {
            Join(current, sentence);
            if (current.Length >= MinLengthOfPiece(pieces.Count))
            {
                pieces.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            if (pieces.Count > 0 && current.Length < FirstPieceMinLength)
            {
                var last = new StringBuilder(pieces[^1]);
                Join(last, current.ToString());
                pieces[^1] = last.ToString();
            }
            else
            {
                pieces.Add(current.ToString());
            }
        }

        return pieces;
    }

    private static int MinLengthOfPiece(int index) => index switch
    {
        0 => FirstPieceMinLength,
        1 => SecondPieceMinLength,
        _ => LaterPieceMinLength
    };

    // Sentences are joined with a space; a line without an end mark (a list item, a heading) keeps its
    // line break, so it is not read as the start of the next sentence.
    private static void Join(StringBuilder piece, string sentence)
    {
        if (piece.Length > 0)
            piece.Append(EndsSentence(piece) ? ' ' : '\n');
        piece.Append(sentence.Trim());
    }

    private static bool EndsSentence(StringBuilder text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c is '"' or '\'' or ')' or ']' or '”' or '’')
                continue;
            return c is '.' or '!' or '?' or '…' or ':' or ';' or ',' or '。' or '！' or '？';
        }

        return false;
    }
}
