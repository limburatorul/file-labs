namespace FileExplorer;

/// filelabs --selftest: the engine over real files in a temp folder, no window. Exit 0 when every check passes; the first
/// failure is printed and in $TMPDIR/filelabs-selftest.txt.
static class SelfTest
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAILED: " + what); }

    static Job Run(Job j) { while (j.Active) { if (j.HasConflicts) j.Resolve(Choice.Rename); Thread.Sleep(20); } return j; }

    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"filelabs-selftest-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("FILELABS_PROFILE", Path.Combine(root, "profile"));
            var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            Directory.CreateDirectory(Path.Combine(src, "tree", "sub"));
            File.WriteAllText(Path.Combine(src, "tree", "a.txt"), "hello");
            File.WriteAllBytes(Path.Combine(src, "tree", "sub", "b.bin"), new byte[3_000_000]);
            File.CreateSymbolicLink(Path.Combine(src, "tree", "link"), "a.txt");

            // copy a tree: files, a folder, a symbolic link made again as a link
            var j = Run(Ferry.Submit(new[] { Path.Combine(src, "tree") }, dst, move: false));
            Check(j.Phase == Phase.Done && j.DoneFiles == 3 && j.Errors.IsEmpty, "copy tree: " + string.Join("; ", j.Errors));
            Check(File.ReadAllText(Path.Combine(dst, "tree", "a.txt")) == "hello" && new FileInfo(Path.Combine(dst, "tree", "sub", "b.bin")).Length == 3_000_000, "copied contents");
            Check(new FileInfo(Path.Combine(dst, "tree", "link")).LinkTarget == "a.txt", "a link stays a link");

            // copying again: every file conflicts, Keep both gives "(2)" names
            j = Run(Ferry.Submit(new[] { Path.Combine(src, "tree") }, dst, move: false));
            Check(j.Phase == Phase.Done && File.Exists(Path.Combine(dst, "tree", "a (2).txt")), "conflict → keep both");

            // names differing only in case are different files on Linux
            File.WriteAllText(Path.Combine(src, "Case.txt"), "upper");
            File.WriteAllText(Path.Combine(dst, "case.txt"), "lower");
            j = Run(Ferry.Submit(new[] { Path.Combine(src, "Case.txt") }, dst, move: false));
            Check(j.Phase == Phase.Done && j.Conflicts.Count == 0 && File.ReadAllText(Path.Combine(dst, "case.txt")) == "lower", "Case.txt and case.txt both kept");

            // a move on one file system is a rename
            j = Run(Ferry.Submit(new[] { Path.Combine(src, "Case.txt") }, dst + "/tree", move: true));
            Check(j.Phase == Phase.Done && File.Exists(Path.Combine(dst, "tree", "Case.txt")) && !File.Exists(Path.Combine(src, "Case.txt")) && j.Notes.Contains("Moved within the same drive"), "same-drive move is a rename");

            // a copy into its own folder is "name - Copy"
            j = Run(Ferry.Submit(new[] { Path.Combine(dst, "case.txt") }, dst, move: false));
            Check(File.Exists(Path.Combine(dst, "case - Copy.txt")), "copy beside: - Copy");

            // verified copy
            Settings.Verify = true;
            j = Run(Ferry.Submit(new[] { Path.Combine(src, "tree", "sub", "b.bin") }, src, move: false));
            Settings.Verify = false;
            Check(j.Phase == Phase.Done && j.Errors.IsEmpty, "verified copy");

            // Trash: in, and back where it was
            var victim = Path.Combine(src, "trash-me.txt");
            File.WriteAllText(victim, "x");
            var inTrash = Trash.Put(victim);
            Check(!File.Exists(victim) && File.Exists(inTrash), "into the Trash");
            Trash.Restore(inTrash);
            Check(File.Exists(victim) && File.ReadAllText(victim) == "x", "restored from the Trash");

            // names in reading order, sizes as on Windows
            var names = new List<string> { "file10", "file2", "File1", "_a" };
            names.Sort(NaturalOrder.Instance);
            Check(names.SequenceEqual(new[] { "_a", "File1", "file2", "file10" }), "natural order: " + string.Join(",", names));
            Check(Fmt.Size(1_500_000) == "1.5 MB" && Fmt.Size(12) == "12 bytes", "sizes");

            // ZIP out and in
            var zip = Path.Combine(root, "t.zip");
            Archives.Zip(new() { Path.Combine(src, "tree") }, zip);
            Archives.Extract(zip, Path.Combine(root, "unzipped"));
            Check(File.ReadAllText(Path.Combine(root, "unzipped", "tree", "a.txt")) == "hello", "zip and extract");

            Console.WriteLine("selftest: all passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "filelabs-selftest.txt"), ex.ToString()); } catch (IOException) { }
            return 1;
        }
        finally { try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
