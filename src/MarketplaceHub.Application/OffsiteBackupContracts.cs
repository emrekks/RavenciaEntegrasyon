using System.Globalization;
using System.Text.RegularExpressions;

namespace MarketplaceHub.Application;

// Sistem Ayarları › Google Drive Yedeği: the server's verified backup sets (deploy/backup/backup.sh) are encrypted with
// the owner's age PUBLIC key and copied to the owner's Google Drive by the worker (OffsiteBackupCopier). These are the
// rules that need no database, network or process: the age key check, the folder and OAuth client checks, which copies
// on Drive retention keeps, and when the next copy is due.
public static partial class OffsiteBackupRules
{
    public const string DefaultFolderName = "Ravencia Yedek";
    public const string FolderModeName = "NAME";
    public const string FolderModeId = "ID";
    public const int DefaultKeepDays = 14;
    public const int MinimumKeepDays = 3;
    public const int MaximumKeepDays = 365;
    public const int DefaultKeepMonths = 12;
    public const int MaximumKeepMonths = 120;
    public const string ObjectSuffix = ".tar.age";

    // drive.file: the app sees and changes only the files and folders it created itself (or the owner opened with it),
    // never the rest of the Drive. openid + email only name the connected account on the panel.
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";
    public const string AuthorizationScopes = "openid email " + DriveFileScope;

    // A started "Google ile Bağlan" must come back within this time, once.
    public static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(10);

    // The copy runs half an hour after the scheduled backups (08:59 and 20:59 UTC, ravencia-backup.timer).
    public static readonly TimeSpan[] DailySlots = [new(9, 30, 0), new(21, 30, 0)];

    // A run that failed for a passing reason is retried after a growing wait: 15 min, 30 min, 1 h, 2 h, then every 4 h.
    public static TimeSpan RetryDelay(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => TimeSpan.FromMinutes(15),
        2 => TimeSpan.FromMinutes(30),
        3 => TimeSpan.FromHours(1),
        4 => TimeSpan.FromHours(2),
        _ => TimeSpan.FromHours(4)
    };

    // After this many failed runs in a row the owner gets one alert e-mail (an invalid Google authorization at once).
    public const int AlertAfterFailures = 3;

    // ── age public key ────────────────────────────────────────────────────────────────────────────────────────────

    private const string Bech32Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    // An age X25519 recipient: "age1" + bech32 of 32 bytes, lower case, with a valid checksum. Returns the person's
    // message for anything else; a private key ("AGE-SECRET-KEY-1...") gets its own warning and is never kept.
    public static string? AgeRecipientError(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0) return "age açık anahtarını girin (age1 ile başlar).";
        if (normalized.StartsWith("AGE-SECRET-KEY-", StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Empty;
            return "Bu bir age ÖZEL anahtarı; buraya yalnız age1 ile başlayan açık anahtar girilir. Özel anahtar sunucuya hiç yazılmaz: bilgisayarınızda ve çevrimdışı bir yerde saklayın.";
        }
        normalized = normalized.ToLowerInvariant();
        return IsAgeRecipient(normalized) ? null : "Geçerli bir age açık anahtarı değil: age1 ile başlayan 62 karakterlik satırın tamamını yapıştırın.";
    }

    public static bool IsAgeRecipient(string value)
    {
        if (value.Length != 62 || !value.StartsWith("age1", StringComparison.Ordinal)) return false;
        var data = new int[value.Length - 4];
        for (var index = 4; index < value.Length; index++)
        {
            var position = Bech32Charset.IndexOf(value[index], StringComparison.Ordinal);
            if (position < 0) return false;
            data[index - 4] = position;
        }
        if (Bech32Polymod([.. ExpandHrp("age"), .. data]) != 1) return false;
        // 52 five-bit groups (without the 6-character checksum) hold the 32-byte X25519 key; the 4 spare bits are zero.
        var payload = data.AsSpan(0, data.Length - 6);
        var bits = 0;
        var accumulator = 0;
        var bytes = 0;
        foreach (var group in payload)
        {
            accumulator = (accumulator << 5) | group;
            bits += 5;
            while (bits >= 8) { bits -= 8; bytes++; }
            accumulator &= (1 << bits) - 1;
        }
        return bytes == 32 && bits < 5 && accumulator == 0;
    }

    private static int[] ExpandHrp(string hrp) => [.. hrp.Select(character => character >> 5), 0, .. hrp.Select(character => character & 31)];

    private static int Bech32Polymod(int[] values)
    {
        int[] generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        var checksum = 1;
        foreach (var value in values)
        {
            var top = checksum >> 25;
            checksum = ((checksum & 0x1ffffff) << 5) ^ value;
            for (var index = 0; index < 5; index++)
                if (((top >> index) & 1) != 0) checksum ^= generator[index];
        }
        return checksum;
    }

    // ── Drive folder ──────────────────────────────────────────────────────────────────────────────────────────────

    // NAME: the app finds or creates a folder of that name at the top of the Drive (drive.file: only one it created).
    // ID: an existing folder's id, which drive.file reaches only when the app created it (a reconnect keeps using it).
    public static string? FolderError(string? mode, string? value, out string normalizedMode, out string normalizedValue)
    {
        normalizedMode = string.Equals(mode?.Trim(), FolderModeId, StringComparison.OrdinalIgnoreCase) ? FolderModeId : FolderModeName;
        normalizedValue = (value ?? string.Empty).Trim();
        if (normalizedMode == FolderModeName)
        {
            if (normalizedValue.Length == 0) normalizedValue = DefaultFolderName;
            return FolderNamePattern().IsMatch(normalizedValue) ? null : "Klasör adı 1–80 karakter olmalı; harf, rakam, boşluk, nokta, tire ve alt çizgi kullanılabilir.";
        }
        var fromLink = FolderLinkPattern().Match(normalizedValue);
        if (fromLink.Success) normalizedValue = fromLink.Groups["id"].Value;
        return FolderIdPattern().IsMatch(normalizedValue) ? null : "Klasör ID'si geçersiz: Drive klasör bağlantısındaki /folders/ sonrasındaki kısmı girin.";
    }

    [GeneratedRegex(@"^[\p{L}\p{N} ._-]{1,80}$")]
    private static partial Regex FolderNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,100}$")]
    private static partial Regex FolderIdPattern();

    [GeneratedRegex(@"/folders/(?<id>[A-Za-z0-9_-]{10,100})")]
    private static partial Regex FolderLinkPattern();

    // ── Google OAuth client (the owner's own, Google Cloud Console › Web application) ─────────────────────────────

    public static bool IsGoogleClientId(string value) => GoogleClientIdPattern().IsMatch(value);

    public static bool IsGoogleClientSecret(string value) => GoogleClientSecretPattern().IsMatch(value);

    [GeneratedRegex(@"^[0-9]{6,30}-[a-z0-9]{10,64}\.apps\.googleusercontent\.com$")]
    private static partial Regex GoogleClientIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,100}$")]
    private static partial Regex GoogleClientSecretPattern();

    public static int ClampKeepDays(int? value) => Math.Clamp(value ?? DefaultKeepDays, MinimumKeepDays, MaximumKeepDays);
    public static int ClampKeepMonths(int? value) => Math.Clamp(value ?? DefaultKeepMonths, 0, MaximumKeepMonths);

    // ── backup sets and their copies ──────────────────────────────────────────────────────────────────────────────

    // A set directory is named by its UTC start time, YYYYMMDDTHHMMSSZ (backup.sh).
    public static bool TryParseSetTime(string name, out DateTimeOffset time) =>
        DateTimeOffset.TryParseExact(name, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time)
        && SetNamePattern().IsMatch(name);

    [GeneratedRegex(@"^[0-9]{8}T[0-9]{6}Z$")]
    private static partial Regex SetNamePattern();

    public static string ObjectName(string setName) => setName + ObjectSuffix;

    // Only "<set>.tar.age" objects are the app's copies; anything else in the folder is never touched.
    public static bool TryParseObjectName(string objectName, out string setName, out DateTimeOffset time)
    {
        setName = string.Empty;
        time = default;
        if (!objectName.EndsWith(ObjectSuffix, StringComparison.Ordinal)) return false;
        var candidate = objectName[..^ObjectSuffix.Length];
        if (!TryParseSetTime(candidate, out time)) return false;
        setName = candidate;
        return true;
    }

    // Retention on Drive: every copy of the last keepDays days, the first copy of each of the last keepMonths calendar
    // months (UTC, the current one included), and always the newest copy. Returns the names to delete; names that are not
    // the app's copies are never listed.
    public static IReadOnlyList<string> SelectForDeletion(IEnumerable<string> objectNames, DateTimeOffset now, int keepDays, int keepMonths)
    {
        var copies = objectNames
            .Select(name => TryParseObjectName(name, out _, out var time) ? (Name: name, Time: time) : (Name: name, Time: (DateTimeOffset?)null))
            .Where(copy => copy.Time is not null)
            .Select(copy => (copy.Name, Time: copy.Time!.Value))
            .Distinct()
            .OrderBy(copy => copy.Time)
            .ToList();
        if (copies.Count == 0) return [];
        var keep = new HashSet<string>(StringComparer.Ordinal) { copies[^1].Name };
        var dailyCutoff = now.ToUniversalTime().AddDays(-ClampKeepDays(keepDays));
        foreach (var copy in copies.Where(copy => copy.Time >= dailyCutoff)) keep.Add(copy.Name);
        var months = ClampKeepMonths(keepMonths);
        if (months > 0)
        {
            var current = now.ToUniversalTime();
            var oldestMonth = current.Year * 12 + current.Month - 1 - (months - 1);
            foreach (var first in copies.GroupBy(copy => copy.Time.Year * 12 + copy.Time.Month - 1).Where(month => month.Key >= oldestMonth).Select(month => month.First()))
                keep.Add(first.Name);
        }
        return copies.Where(copy => !keep.Contains(copy.Name)).Select(copy => copy.Name).ToList();
    }

    // ── schedule ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static DateTimeOffset LastSlotAtOrBefore(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var day = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        for (var offset = 0; offset < 3; offset++)
        {
            var date = day.AddDays(-offset);
            foreach (var slot in DailySlots.OrderByDescending(x => x))
                if (date + slot <= utc) return date + slot;
        }
        return day.AddDays(-2);
    }

    public static DateTimeOffset NextSlotAfter(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var day = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        for (var offset = 0; offset < 3; offset++)
        {
            var date = day.AddDays(offset);
            foreach (var slot in DailySlots.OrderBy(x => x))
                if (date + slot > utc) return date + slot;
        }
        return day.AddDays(2);
    }

    // When the next copy runs; null while it cannot run (switched off, not connected, Google authorization invalid).
    // A request from the panel (Şimdi Gönder) runs at once; a failed run is retried after RetryDelay; otherwise the copy
    // runs at the next slot after the last attempt (and at once when it never ran).
    public static DateTimeOffset? NextRunAt(OffsiteBackupScheduleState state, DateTimeOffset now)
    {
        if (!state.Enabled || !state.Ready || state.AuthorizationInvalid) return null;
        if (state.RequestedAt is { } requested && (state.LastAttemptAt is null || requested > state.LastAttemptAt)) return requested < now ? now : requested;
        if (state.LastAttemptAt is not { } lastAttempt) return now;
        if (state.ConsecutiveFailures > 0)
        {
            var retry = lastAttempt + RetryDelay(state.ConsecutiveFailures);
            var slot = NextSlotAfter(lastAttempt);
            return retry < slot ? retry : slot;
        }
        return NextSlotAfter(lastAttempt);
    }
}

// Ready: the age key, the OAuth client and a Google authorization are all saved.
public sealed record OffsiteBackupScheduleState(bool Enabled, bool Ready, bool AuthorizationInvalid, DateTimeOffset? LastAttemptAt, int ConsecutiveFailures, DateTimeOffset? RequestedAt);
