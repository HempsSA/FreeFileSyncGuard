using SyncGuard.Models;

namespace SyncGuard.Services;

/// <summary>
/// Ransomware detection: Shannon entropy sampling, suspicious-extension
/// detection (223+ known families), composite anomaly scoring.
/// Ports ransomware.py.
/// </summary>
public static class RansomwareService
{
    public static readonly HashSet<string> SuspiciousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // WannaCry / WCry
        ".wnry", ".wcry", ".wncry", ".wncryt",
        // Locky
        ".locky", ".zepto", ".odin", ".thor", ".aesir", ".diablo6",
        ".cerber", ".cerber3", ".cerber5", ".cerber6",
        ".abc", ".ccc", ".vvv", ".ttt", ".ecc", ".ezz", ".exx",
        ".xyz", ".zzz", ".zzzzz", ".micro", ".xxx",
        // Dharma / CrySIS
        ".dharma", ".wallet", ".arena", ".bip", ".cobra",
        ".java", ".arrow", ".brrr", ".boost", ".gamma",
        ".monro", ".cezar", ".bleep", ".,onion",
        ".eth", ".crab", ".deb", ".frozen", ".betta",
        ".audit", ".virs", ".lisa", ".phobos",
        ".eking", ".eight", ".ethylamerica", ".makop",
        ".mkp", ".decoder", ".mira", ".flavor", ".emprise",
        // CryptoLocker / CryptoWall
        ".crypt", ".crypto", ".encrypted", ".locked",
        ".crypted", ".crypz", ".crypt1", ".crypt2", ".crypt3",
        ".cryptolocker", ".cryptowall",
        // GlobeImposter
        ".auchentoshan", ".auodsi", ".bad", ".cod",
        // STOP / Djvu
        ".moia", ".ness", ".omba", ".loce", ".vawai",
        ".boothe", ".lanset", ".kaak", ".moka",
        ".medusa", ".stare", ".lote", ".krogu",
        ".karl", ".wand", ".mol64", ".olgun", ".lkfr",
        ".deria", ".masodas", ".bandar", ".tro",
        ".gero", ".befro", ".liy0", ".nyton", ".ryeco",
        ".liquido", ".allead", ".alcat", ".moba", ".nusm",
        ".kyra", ".vega", ".mogera", ".udia",
        ".kodg", ".zqqw", ".lecho", ".varies",
        ".szig", ".coharos", ".blocked",
        // Conti / Ryuk / REvil
        ".conti", ".ryuk", ".revil", ".sodinokibi",
        ".rkhorse", ".rmar", ".booa", ".elbie",
        ".devos", ".lukits", ".mekos",
        // Maze / Egregor / NetWalker
        ".maze", ".egregor", ".netwalker", ".cryptomix",
        ".meow", ".enc",
        // Avaddon
        ".avdn", ".abensen",
        // BlackCat / ALPHV
        ".blackcat", ".alphv",
        // LockBit
        ".lockbit", ".lockbit3.0", ".lockbit2",
        // Hive
        ".hive", ".key", ".key.hive",
        // Clop
        ".clop", ".cl0p",
        // Akira
        ".akira",
        // MedusaLocker
        ".readtheinstructions", ".readinstruction",
        // Mount Locker
        ".lived", ".pazd",
        // Maoloa
        ".maoloa", ".harma", ".loa",
        // Magniber
        ".kinopoisk", ".dodocool",
        // STOP variants
        ".puma", ".luna", ".sald",
        // Generic / misc
        ".encc", ".ransom", ".ransomed",
        ".vault", ".cryb2", ".ctb2", ".ctbl",
        ".crinf", ".crjoker", ".darkness", ".frtrss",
        ".good", ".ha3", ".hydracrypt", ".kb15",
        ".kraken", ".lechiffre", ".lockedup", ".magic",
        ".nochance", ".omg!", ".lol!", ".pay", ".paym",
        ".r5a", ".rdm", ".rrk", ".sdjn", ".supercrypt",
        ".toxcrypt", ".bos", ".gdb",
        ".abyss", ".matrix", ".nightcrow",
        ".ping", ".quantum", ".snet",
        ".tprc", ".unkno", ".xam",
        ".lukitus", ".xrnt", ".xtbl",
        ".cry",
        // Numbered / zero-padded
        ".0x0", ".1999", ".000", ".111", ".222", ".333",
        ".444", ".555", ".666", ".777", ".888", ".999",
    };

    public static double ShannonEntropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0.0;
        Span<int> counts = stackalloc int[256];
        foreach (var b in data) counts[b]++;
        double entropy = 0.0;
        double len = data.Length;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0) continue;
            double p = counts[i] / len;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    public static double? FileEntropy(string path, int sampleSize = 4096)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(sampleSize, (int)Math.Min(fs.Length, sampleSize))];
            if (buf.Length == 0) return 0.0;
            int read = fs.Read(buf, 0, buf.Length);
            return ShannonEntropy(buf.AsSpan(0, read));
        }
        catch { return null; }
    }

    public static EntropyResult SampleEntropy(IReadOnlyList<string> filePaths, int sampleCount = 20, double threshold = 7.5, int sampleSize = 4096)
    {
        if (filePaths.Count == 0) return new EntropyResult();
        var rng = Random.Shared;
        var toSample = filePaths.Count <= sampleCount
            ? filePaths.ToList()
            : filePaths.OrderBy(_ => rng.Next()).Take(sampleCount).ToList();

        var entropies = new List<double>();
        var flagged = new List<string>();
        foreach (var fp in toSample)
        {
            var ent = FileEntropy(fp, sampleSize);
            if (ent is null) continue;
            entropies.Add(ent.Value);
            if (ent.Value >= threshold) flagged.Add(fp);
        }
        if (entropies.Count == 0) return new EntropyResult();
        return new EntropyResult
        {
            Sampled = entropies.Count,
            HighEntropy = flagged.Count,
            AvgEntropy = Math.Round(entropies.Average(), 3),
            MaxEntropy = Math.Round(entropies.Max(), 3),
            FlaggedFiles = flagged,
            IsSuspicious = flagged.Count >= Math.Max(2, entropies.Count / 5),
        };
    }

    public static ExtensionResult DetectSuspiciousExtensions(
        IReadOnlyList<string> changedFiles,
        IEnumerable<string>? extraBlocklist = null,
        double thresholdPct = 5.0)
    {
        if (changedFiles.Count == 0) return new ExtensionResult();
        var blocklist = new HashSet<string>(SuspiciousExtensions, StringComparer.OrdinalIgnoreCase);
        if (extraBlocklist is not null)
            foreach (var e in extraBlocklist) blocklist.Add(e.StartsWith('.') ? e : "." + e);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var flagged = new List<string>();
        foreach (var fp in changedFiles)
        {
            var ext = Path.GetExtension(fp);
            if (!string.IsNullOrEmpty(ext) && blocklist.Contains(ext))
            {
                counts[ext] = counts.TryGetValue(ext, out var c) ? c + 1 : 1;
                flagged.Add(fp);
            }
        }
        double pct = changedFiles.Count > 0 ? (double)flagged.Count / changedFiles.Count * 100 : 0;
        return new ExtensionResult
        {
            TotalChanged = changedFiles.Count,
            Suspicious = flagged.Count,
            SuspiciousExts = counts,
            FlaggedFiles = flagged,
            IsSuspicious = pct >= thresholdPct,
        };
    }

    /// <summary>
    /// Weighted composite: change 0-40 + entropy 0/25 + extensions 0/20
    /// + deletes 0-15 + renames 0-10. Block when &gt; threshold.
    /// </summary>
    public static AnomalyScore ComputeAnomalyScore(
        double changePct, long totalFiles, long changedFiles,
        long deletedFiles = 0, long renamedFiles = 0,
        EntropyResult? entropyResult = null,
        ExtensionResult? extensionResult = null,
        double blockThreshold = 60.0,
        double wChange = 0.4, double wEntropy = 25.0,
        double wExtension = 20.0, double wDelete = 15.0, double wRename = 10.0)
    {
        var reasons = new List<string>();
        double changeComponent = changePct * wChange;
        if (changePct > 30) reasons.Add($"High change rate: {changePct:F1}%");

        bool entropyFlag = entropyResult?.IsSuspicious == true;
        double entropyComponent = entropyFlag ? wEntropy : 0.0;
        if (entropyFlag) reasons.Add($"High-entropy (encrypted) files detected: {entropyResult!.HighEntropy}/{entropyResult.Sampled}");

        bool extFlag = extensionResult?.IsSuspicious == true;
        double extComponent = extFlag ? wExtension : 0.0;
        if (extFlag)
        {
            var exts = string.Join(", ", extensionResult!.SuspiciousExts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})"));
            reasons.Add("Suspicious extensions: " + exts);
        }

        double deleteRatio = totalFiles > 0 ? (double)deletedFiles / totalFiles * 100 : 0;
        double deleteComponent = Math.Min(deleteRatio, 100.0) / 100.0 * wDelete;
        if (deleteRatio > 10) reasons.Add($"Mass deletions: {deleteRatio:F1}%");

        double renameRatio = totalFiles > 0 ? (double)renamedFiles / totalFiles * 100 : 0;
        double renameComponent = Math.Min(renameRatio, 100.0) / 100.0 * wRename;
        if (renameRatio > 10) reasons.Add($"Mass renames: {renameRatio:F1}%");

        double total = changeComponent + entropyComponent + extComponent + deleteComponent + renameComponent;
        return new AnomalyScore
        {
            ChangeRate = changePct,
            EntropyFlag = entropyFlag,
            ExtensionFlag = extFlag,
            DeleteRatio = Math.Round(deleteRatio, 2),
            RenameRatio = Math.Round(renameRatio, 2),
            Score = Math.Round(total, 2),
            IsBlocked = total > blockThreshold,
            Reasons = reasons,
            EntropyResult = entropyResult,
            ExtensionResult = extensionResult,
        };
    }
}
