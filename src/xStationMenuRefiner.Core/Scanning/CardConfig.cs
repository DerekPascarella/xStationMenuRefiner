using System;
using System.IO;
using xStationMenuRefiner.Core.Model;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Scanning;

// The single byte of flags xStation keeps in 00xstation/config.txt.
//
// Confirmed against three cards on 2026-07-30. Toggling Folder Browsing moved bit 5 and
// nothing else, and changing Loader Video Mode moved bit 3 instead.
//
//     0x03  browsing off, video auto
//     0x23  browsing on,  video auto
//     0x2B  browsing on,  video NTSC
public sealed class CardConfig
{
    public const byte FolderBrowsingFlag = 0x20;

    public byte Flags { get; private init; }

    // False when the card has no readable config, in which case Mode is only a guess.
    public bool WasDetected { get; private init; }

    // The firmware's built-in default byte is 0x23, so a card with no config runs with
    // Folder Browsing on.
    public MenuMode Mode =>
        !WasDetected || (Flags & FolderBrowsingFlag) != 0 ? MenuMode.Browse : MenuMode.Flat;

    public static CardConfig Read(string rootPath)
    {
        string path;

        try
        {
            path = Path.Combine(rootPath, RemovableDrives.SystemFolderName, "config.txt");
        }
        catch (ArgumentException)
        {
            return new CardConfig();
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.Length == 0)
                return new CardConfig();

            return new CardConfig { Flags = bytes[0], WasDetected = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new CardConfig();
        }
    }
}
