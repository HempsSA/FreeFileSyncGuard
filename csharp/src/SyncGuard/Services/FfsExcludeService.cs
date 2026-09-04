using System.Security;
using System.Text;
using System.Xml.Linq;

namespace SyncGuard.Services;

/// <summary>
/// FreeFileSync exclude-filter tool. Ports ffs_exclude_setup.py:
/// merge 34 Windows exclusion patterns into .ffs_batch/.ffs_gui XML,
/// preview (dry-run), scan folders, create template batch files.
/// </summary>
public static class FfsExcludeService
{
    public static readonly IReadOnlyList<string> WindowsExcludes = new[]
    {
        @"\System Volume Information\",
        @"\$Recycle.Bin\",
        @"\RECYCLER\",
        @"\RECYCLE?\",
        @"\Recovery\",
        @"\WinSxS\",
        @"\SoftwareDistribution\",
        @"\Installer\",
        @"\Windows\Temp\",
        @"\Windows\Prefetch\",
        @"\Windows\Installer\$PatchCache$\",
        @"*\thumbs.db",
        @"*\desktop.ini",
        "*.tmp",
        "*.temp",
        "~$*",
        "~*.*",
        @"*\msdownld.tmp",
        "*.log",
        "*.etl",
        "*.evtx",
        "*.lnk",
        @"\.git\",
        @"\.svn\",
        @"\.hg\",
        "*.DS_Store",
        @"\._*",
        @"\__MACOSX\",
        ".Trash-*",
        @".cache\",
        @"*\Cache\",
        @"*\cache2\",
        @"*\Service Worker\",
        @"\syncguard_cache\",
    };

    public sealed record ProcessResult(bool Changed, int Added, List<string> Merged, string? Error);

    public static List<string> GetExisting(string file)
    {
        var root = XDocument.Load(file, LoadOptions.PreserveWhitespace).Root!;
        return root.Descendants("Filter").Elements("Exclude").Elements("Item")
            .Select(e => (e.Value ?? "").Trim())
            .Where(v => v.Length > 0).ToList();
    }

    public static (List<string> Merged, int Added) Merge(IEnumerable<string> existing, IEnumerable<string>? additions)
    {
        var seen = new HashSet<string>(existing.Select(e => e.ToLowerInvariant()));
        var merged = existing.ToList();
        int added = 0;
        foreach (var p in additions ?? WindowsExcludes)
        {
            if (seen.Add(p.ToLowerInvariant())) { merged.Add(p); added++; }
        }
        return (merged, added);
    }

    public static ProcessResult ProcessFile(string file, IEnumerable<string>? filters = null, bool dryRun = false)
    {
        filters ??= WindowsExcludes;
        XDocument doc;
        try { doc = XDocument.Load(file, LoadOptions.PreserveWhitespace); }
        catch (Exception ex) { return new ProcessResult(false, 0, new List<string>(), ex.Message); }
        var root = doc.Root!;
        var existing = GetExisting(file);
        var (merged, added) = Merge(existing, filters);
        if (added == 0) return new ProcessResult(false, 0, merged, null);
        ApplyToXml(root, merged);
        if (!dryRun)
        {
            var backup = file + ".bak";
            if (!File.Exists(backup)) File.Copy(file, backup);
            var settings = new System.Xml.XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\n",
            };
            using var writer = System.Xml.XmlWriter.Create(file, settings);
            doc.Save(writer);
        }
        return new ProcessResult(true, added, merged, null);
    }

    private static void ApplyToXml(XElement root, List<string> merged)
    {
        var filt = root.Element("Filter");
        if (filt is null)
        {
            filt = new XElement("Filter");
            var anchor = root.Elements().FirstOrDefault(e => e.Name == "Synchronize" || e.Name == "Compare");
            if (anchor is null) root.Add(filt);
            else anchor.AddAfterSelf(filt);
        }
        var exc = filt.Element("Exclude");
        if (exc is null)
        {
            exc = new XElement("Exclude");
            var inc = filt.Element("Include");
            if (inc is null) filt.Add(exc);
            else inc.AddAfterSelf(exc);
        }
        exc.Elements("Item").Remove();
        foreach (var p in merged) exc.Add(new XElement("Item", p));
    }

    public static List<string> ScanFolder(string folder)
    {
        if (!Directory.Exists(folder)) return new List<string>();
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ffs_batch", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".ffs_gui", StringComparison.OrdinalIgnoreCase))
            .Order().ToList();
    }

    public static void CreateTemplate(string file, string source, string target,
        IEnumerable<string>? filters = null, bool dryRun = false)
    {
        filters ??= WindowsExcludes;
        string Esc(string s) => SecurityElement.Escape(s) ?? s;
        var items = string.Join("\n", filters.Select(p => $"      <Item>{Esc(p)}</Item>"));
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <FreeFileSync XmlType="BATCH" XmlFormat="23">
              <Notes>Auto-generated with Windows exclude filters</Notes>
              <Compare>
                <Variant>TimeAndSize</Variant>
                <Symlinks>Exclude</Symlinks>
                <IgnoreTimeShift/>
              </Compare>
              <Synchronize>
                <Changes>
                  <Left Create="right" Update="right" Delete="none"/>
                  <Right Create="none" Update="right" Delete="none"/>
                </Changes>
                <DeletionPolicy>RecycleBin</DeletionPolicy>
                <VersioningFolder Style="Replace"/>
              </Synchronize>
              <Filter>
                <Include>
                  <Item>*</Item>
                </Include>
                <Exclude>
            {items}
                </Exclude>
                <SizeMin Unit="None">0</SizeMin>
                <SizeMax Unit="None">0</SizeMax>
                <TimeSpan Type="None">0</TimeSpan>
              </Filter>
              <FolderPairs>
                <Pair>
                  <Left Threads="4">{Esc(source)}</Left>
                  <Right Threads="4">{Esc(target)}</Right>
                </Pair>
              </FolderPairs>
              <Errors Ignore="true" Retry="2" Delay="1"/>
              <PostSyncCommand Condition="Completion"/>
              <LogFolder></LogFolder>
              <EmailNotification Condition="Never"/>
              <GridViewType>Action</GridViewType>
              <Batch>
                <ProgressDialog Minimized="true" AutoClose="true"/>
                <ErrorDialog>Show</ErrorDialog>
                <PostSyncAction>None</PostSyncAction>
              </Batch>
            </FreeFileSync>
            """;
        if (!dryRun) File.WriteAllText(file, xml.Replace("\n", "\r\n"), new UTF8Encoding(false));
    }
}
