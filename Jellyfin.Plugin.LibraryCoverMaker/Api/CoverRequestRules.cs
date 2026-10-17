using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using SkiaSharp;

namespace Jellyfin.Plugin.LibraryCoverMaker.Api;

/// <summary>
/// What the admin API accepts, apart from what only Jellyfin can answer (does this title exist, may every viewer see
/// it). A value outside its range is refused with a message that names it; nothing is clamped, so a request never
/// saves something other than what it said.
/// </summary>
internal static class CoverRequestRules
{
    /// <summary>The largest cover an admin can upload.</summary>
    internal const long MaxUploadBytes = 20L * 1024 * 1024;

    /// <summary>How many bytes at the start of a file <see cref="DetectImageType"/> needs.</summary>
    internal const int HeaderLength = 16;

    /// <summary>The largest width or height of an uploaded cover, in pixels.</summary>
    internal const int MaxImageSide = 4096;

    internal const int MaxTitleLength = 100;
    internal const int MaxRenewalDays = 365;

    private const int MinTextSize = 40;
    private const int MaxTextSize = 200;
    private const int MinDarken = 0;
    private const int MaxDarken = 70;
    private const int MinLength = 2;
    private const int MaxLength = 30;
    private const int MinWidth = 320;
    private const int MaxWidth = 1920;

    internal static bool IsDefined<T>(T value)
        where T : struct, Enum => Enum.IsDefined(value);

    /// <summary>The problems with a style change, one message each. Empty when the change is acceptable.</summary>
    internal static IReadOnlyList<string> CheckStyle(StyleUpdateDto style)
    {
        ArgumentNullException.ThrowIfNull(style);

        var errors = new List<string>();
        if (style.Title is { } title && (title.Length > MaxTitleLength || title.Any(char.IsControl)))
        {
            errors.Add($"Title must be at most {MaxTitleLength} characters, without control characters.");
        }

        if (style.Font is not null && FontFamily(style.Font) is null)
        {
            errors.Add($"Font must be one of: {string.Join(", ", FontCatalog.Families)}.");
        }

        Range(errors, nameof(style.TextSize), style.TextSize, MinTextSize, MaxTextSize);
        Range(errors, nameof(style.Darken), style.Darken, MinDarken, MaxDarken);
        Range(errors, nameof(style.LengthSeconds), style.LengthSeconds, MinLength, MaxLength);
        Range(errors, nameof(style.SlotCount), style.SlotCount, 1, LibraryCoverState.MaxSlots);
        return errors;
    }

    /// <summary>Copies the fields <paramref name="style"/> gives onto <paramref name="target"/>. Check first.</summary>
    internal static void ApplyStyle(StyleUpdateDto style, CoverStyle target)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(target);

        target.Title = style.Title?.Trim() ?? target.Title;
        target.Font = style.Font is null ? target.Font : FontFamily(style.Font) ?? target.Font;
        target.TextSize = style.TextSize ?? target.TextSize;
        target.Darken = style.Darken ?? target.Darken;
        target.Frame = style.Frame ?? target.Frame;
        target.Shadow = style.Shadow ?? target.Shadow;
        target.LengthSeconds = style.LengthSeconds ?? target.LengthSeconds;
        target.Fade = style.Fade ?? target.Fade;
        target.SlotCount = style.SlotCount ?? target.SlotCount;
        target.UseMostWatched = style.UseMostWatched ?? target.UseMostWatched;
        target.UseTopRated = style.UseTopRated ?? target.UseTopRated;
        target.UseRecent = style.UseRecent ?? target.UseRecent;
    }

    /// <summary>Why the renewal days are not acceptable, or null. Zero is accepted: it clears the timetable.</summary>
    internal static string? CheckRenewal(int? days) =>
        days is < 0 or > MaxRenewalDays
            ? $"RenewEveryDays must be 1 to {MaxRenewalDays}, or 0 to follow the weekly task."
            : null;

    /// <summary>
    /// The problems with a new set of slots: not exactly five, a title locked twice (the picker would keep the first
    /// and silently free the other), a moment or backdrop that makes no sense, or whatever
    /// <paramref name="problemWithLock"/> says of a locked slot (a title Jellyfin does not have in this library, or
    /// one some viewer may not see). It is asked once per locked slot, only when the slot is otherwise fine.
    /// </summary>
    internal static IReadOnlyList<string> CheckSlots(
        SlotUpdateDto[] slots,
        Func<SlotUpdateDto, string?> problemWithLock)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(problemWithLock);

        if (slots.Length != LibraryCoverState.MaxSlots || slots.Any(slot => slot is null))
        {
            return [$"Slots must hold exactly {LibraryCoverState.MaxSlots} slots, in order."];
        }

        var errors = new List<string>();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            var number = i + 1;
            if (slot.ItemId is null)
            {
                if (slot.MomentSeconds is not null || slot.BackdropIndex is not null)
                {
                    errors.Add($"Slot {number} is free, so it cannot have a moment or a backdrop.");
                }

                continue;
            }

            var before = errors.Count;
            if (slot.ItemId == Guid.Empty)
            {
                errors.Add($"Slot {number} has an empty item id.");
            }
            else if (!seen.Add(slot.ItemId.Value))
            {
                errors.Add($"Slot {number} locks a title that another slot locks too; a title is in a cover once.");
            }

            if (slot.MomentSeconds is { } moment && (!double.IsFinite(moment) || moment < 0))
            {
                errors.Add($"Slot {number} has a moment that is not a time from the start of the video.");
            }

            if (slot.BackdropIndex is < 0)
            {
                errors.Add($"Slot {number} has a negative backdrop index.");
            }

            if (slot.MomentSeconds is not null && slot.BackdropIndex is not null)
            {
                errors.Add($"Slot {number} has both a moment and a backdrop; a slot uses one of them.");
            }

            if (errors.Count == before && problemWithLock(slot) is { } problem)
            {
                errors.Add($"Slot {number}: {problem}");
            }
        }

        return errors;
    }

    /// <summary>Writes the slots, which <see cref="CheckSlots"/> accepted, into the state's own slots.</summary>
    internal static void ApplySlots(SlotUpdateDto[] slots, CoverSlot[] target)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(target);

        for (var i = 0; i < target.Length; i++)
        {
            target[i] = new CoverSlot
            {
                ItemId = slots[i].ItemId,
                MomentSeconds = slots[i].ItemId is null ? null : slots[i].MomentSeconds,
                BackdropIndex = slots[i].ItemId is null ? null : slots[i].BackdropIndex,
            };
        }
    }

    /// <summary>The problems with a settings change, judged against what the configuration holds now.</summary>
    internal static IReadOnlyList<string> CheckSettings(SettingsUpdateDto update, PluginConfiguration current)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(current);

        var errors = new List<string>();
        if (update.PrimaryFormat is { } format && !IsDefined(format))
        {
            errors.Add("PrimaryFormat must be Animated or Still.");
        }

        if (update.DefaultMode is { } mode && !IsDefined(mode))
        {
            errors.Add("DefaultMode must be Art, Scenes or Manual.");
        }

        if (update.OutputWidth is not null || update.OutputHeight is not null)
        {
            var width = update.OutputWidth ?? current.OutputWidth;
            var height = update.OutputHeight ?? current.OutputHeight;
            var sound = width is >= MinWidth and <= MaxWidth
                && width % 2 == 0
                && height % 2 == 0
                && width * 9 == height * 16;
            if (!sound)
            {
                errors.Add(
                    $"The output size must be 16 by 9, even, and {MinWidth} to {MaxWidth} pixels wide "
                    + $"(for example 640 by 360 or 960 by 540), not {width} by {height}.");
            }
        }

        return errors;
    }

    /// <summary>
    /// The MIME type of the image whose first bytes are <paramref name="header"/>, judged by the signature alone, or
    /// null when it is not WebP, PNG, JPEG or GIF. What the client says the file is does not count: a page, a script
    /// or an executable renamed to .png has none of these signatures.
    /// </summary>
    internal static string? DetectImageType(ReadOnlySpan<byte> header)
    {
        ReadOnlySpan<byte> png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];
        if (header.StartsWith(png))
        {
            return "image/png";
        }

        if (header.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff]))
        {
            return "image/jpeg";
        }

        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        // RIFF <size> WEBP <chunk>: the chunk is lossy (VP8 ), lossless (VP8L) or extended (VP8X, animations).
        if (header.Length >= 16
            && header.StartsWith("RIFF"u8)
            && header.Slice(8, 4).SequenceEqual("WEBP"u8)
            && (header.Slice(12, 4).SequenceEqual("VP8 "u8)
                || header.Slice(12, 4).SequenceEqual("VP8L"u8)
                || header.Slice(12, 4).SequenceEqual("VP8X"u8)))
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>
    /// Reads the header of an uploaded image (no pixels) and says why it cannot be a cover, or returns null: the
    /// bytes are not an image the decoder can open, they are another format than <paramref name="mimeType"/> (which
    /// came from the signature), or a side is longer than <see cref="MaxImageSide"/> pixels, which Jellyfin's image
    /// processor would decode on every request. The stream is rewound to the start.
    /// </summary>
    internal static string? CheckImage(Stream stream, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(stream);

        stream.Position = 0;
        try
        {
            // The managed stream does not own the caller's stream.
            using var source = new SKManagedStream(stream, disposeManagedStream: false);
            using var codec = SKCodec.Create(source);
            if (codec is null)
            {
                return "The file is not a complete image that can be read.";
            }

            var expected = mimeType switch
            {
                "image/png" => SKEncodedImageFormat.Png,
                "image/jpeg" => SKEncodedImageFormat.Jpeg,
                "image/gif" => SKEncodedImageFormat.Gif,
                "image/webp" => SKEncodedImageFormat.Webp,
                _ => (SKEncodedImageFormat?)null,
            };
            if (codec.EncodedFormat != expected)
            {
                return "The file's contents are not the kind of image its first bytes say.";
            }

            if (codec.Info.Width > MaxImageSide || codec.Info.Height > MaxImageSide)
            {
                return $"The image is {codec.Info.Width} by {codec.Info.Height} pixels; a cover can be at most "
                    + $"{MaxImageSide} by {MaxImageSide}.";
            }

            return null;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    // The catalog's own spelling of a family, whatever the case the client used.
    private static string? FontFamily(string name) =>
        FontCatalog.Families.FirstOrDefault(family => string.Equals(family, name, StringComparison.OrdinalIgnoreCase));

    private static void Range(List<string> errors, string field, int? value, int min, int max)
    {
        if (value is { } number && (number < min || number > max))
        {
            errors.Add($"{field} must be {min} to {max}, not {number}.");
        }
    }
}
