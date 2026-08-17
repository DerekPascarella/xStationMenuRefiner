using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Model;

// One FILE line in a CUE sheet. The character range lets a rewrite splice over the name
// and leave every other byte alone.
public sealed class CueFileReference
{
    public string Name { get; set; } = "";
    public string Format { get; set; } = "BINARY";
    public bool Quoted { get; set; } = true;
    public int NameStart { get; set; }
    public int NameLength { get; set; }
    public int LineNumber { get; set; }
}

public sealed class CueTrack
{
    public int Number { get; set; }
    public string Mode { get; set; } = "";
    public int FileIndex { get; set; } = -1;
    public int LineNumber { get; set; }

    public bool IsAudio =>
        Mode.StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase);
}

// A parsed CUE sheet that remembers the exact text it came from.
public sealed class CueDocument
{
    private readonly string _text;
    private readonly Encoding _encoding;

    public string Path { get; }
    public List<CueFileReference> Files { get; } = new();
    public List<CueTrack> Tracks { get; } = new();

    private CueDocument(string path, string text, Encoding encoding)
    {
        Path = path;
        _text = text;
        _encoding = encoding;
    }

    // Latin-1 maps every byte value to a distinct character, so a CUE holding bytes that
    // are not valid UTF-8 still round-trips unchanged.
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding LenientUtf8 = new(false, false);

    public static CueDocument Load(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        return Parse(path, raw);
    }

    public static CueDocument Parse(string path, byte[] raw)
    {
        string text;
        Encoding encoding;

        try
        {
            text = StrictUtf8.GetString(raw);
            encoding = LenientUtf8;
        }
        catch (DecoderFallbackException)
        {
            text = Latin1.GetString(raw);
            encoding = Latin1;
        }

        var doc = new CueDocument(path, text, encoding);
        doc.ParseLines();
        return doc;
    }

    private void ParseLines()
    {
        int position = 0;
        int lineNumber = 0;
        int currentFileIndex = -1;

        while (position < _text.Length)
        {
            int lineEnd = _text.IndexOf('\n', position);
            int lineLength = lineEnd < 0 ? _text.Length - position : lineEnd - position;
            string line = _text.Substring(position, lineLength);
            lineNumber++;

            int trimmed = CountLeadingWhitespace(line);
            string body = line.Substring(trimmed).TrimEnd('\r', ' ', '\t');

            if (body.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
            {
                var reference = ParseFileLine(body, position + trimmed, lineNumber);
                if (reference != null)
                {
                    Files.Add(reference);
                    currentFileIndex = Files.Count - 1;
                }
            }
            else if (body.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
            {
                var track = ParseTrackLine(body, lineNumber);
                if (track != null)
                {
                    track.FileIndex = currentFileIndex;
                    Tracks.Add(track);
                }
            }

            if (lineEnd < 0)
                break;

            position = lineEnd + 1;
        }
    }

    private static int CountLeadingWhitespace(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;
        return i;
    }

    // Accepts a quoted name, and an unquoted single-token name for CUEs written by hand.
    private static CueFileReference? ParseFileLine(string body, int bodyOffset, int lineNumber)
    {
        int cursor = 4;
        while (cursor < body.Length && (body[cursor] == ' ' || body[cursor] == '\t'))
            cursor++;

        if (cursor >= body.Length)
            return null;

        string name;
        int nameStart;
        bool quoted;

        if (body[cursor] == '"')
        {
            int close = body.IndexOf('"', cursor + 1);
            if (close < 0)
                return null;

            nameStart = cursor + 1;
            name = body.Substring(nameStart, close - nameStart);
            quoted = true;
            cursor = close + 1;
        }
        else
        {
            int end = cursor;
            while (end < body.Length && body[end] != ' ' && body[end] != '\t')
                end++;

            nameStart = cursor;
            name = body.Substring(nameStart, end - nameStart);
            quoted = false;
            cursor = end;
        }

        string format = body.Substring(Math.Min(cursor, body.Length)).Trim();

        return new CueFileReference
        {
            Name = name,
            Format = string.IsNullOrEmpty(format) ? "BINARY" : format,
            Quoted = quoted,
            NameStart = bodyOffset + nameStart,
            NameLength = name.Length,
            LineNumber = lineNumber,
        };
    }

    private static CueTrack? ParseTrackLine(string body, int lineNumber)
    {
        var parts = body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return null;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return null;

        return new CueTrack
        {
            Number = number,
            Mode = parts[2],
            LineNumber = lineNumber,
        };
    }

    // The FILE entry backing the first track that carries data. xStation takes the menu
    // label from this one.
    public CueFileReference? FirstDataTrackFile()
    {
        foreach (var track in Tracks)
        {
            if (track.IsAudio)
                continue;

            if (track.FileIndex >= 0 && track.FileIndex < Files.Count)
                return Files[track.FileIndex];
        }

        return Files.Count > 0 ? Files[0] : null;
    }

    public IEnumerable<CueTrack> TracksForFile(int fileIndex)
    {
        foreach (var track in Tracks)
        {
            if (track.FileIndex == fileIndex)
                yield return track;
        }
    }

    // Rewrites the given file names in place. Every other byte of the CUE survives,
    // including line endings, indentation and any encoding quirk.
    public byte[] Rewrite(IReadOnlyDictionary<string, string> replacements)
    {
        var builder = new StringBuilder(_text.Length + 64);
        int copied = 0;

        foreach (var reference in Files)
        {
            if (!replacements.TryGetValue(reference.Name, out string? updated))
                continue;

            if (updated == null || string.Equals(updated, reference.Name, StringComparison.Ordinal))
                continue;

            builder.Append(_text, copied, reference.NameStart - copied);
            builder.Append(updated);
            copied = reference.NameStart + reference.NameLength;
        }

        builder.Append(_text, copied, _text.Length - copied);

        return _encoding.GetBytes(builder.ToString());
    }
}
