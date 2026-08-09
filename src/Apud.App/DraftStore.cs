using System.Security.Cryptography;
using System.Text;
using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.App;

/// <summary>
/// Drafts are the cataloguer's working scratchpad — records being composed that
/// are NOT yet in the catalogue. They live as <c>.mrk</c> files under
/// <c>%APPDATA%\Apud\drafts\&lt;catalogue-key&gt;\</c>, one file per draft, so they
/// survive a close/reopen without ever touching <c>catalog.db</c> (user, 2026-08-08:
/// "drafts should NOT be uploaded to the db… they should be written to Apud's app
/// files"). Ctrl+D writes here; catalogue open reloads them into the sidebar; a
/// push commits the record into the DB and deletes its draft file.
///
/// The folder is keyed to the catalogue's full path, so two catalogues keep
/// separate drafts. Best-effort throughout: a corrupt or unreadable draft file is
/// skipped, never a crash — a scratchpad is not worth an error dialog.
/// </summary>
public sealed class DraftStore
{
    private readonly string _dir;

    public DraftStore(string catalogPath) : this(catalogPath, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apud", "drafts"))
    { }

    internal DraftStore(string catalogPath, string root) => _dir = Path.Combine(root, Key(catalogPath));

    /// <summary>Every saved draft for this catalogue: its file id, base ("BIB"/"AUT")
    /// and record. Skips any file that will not parse.</summary>
    public IEnumerable<(string DraftId, string Base, MarcRecord Record)> LoadAll()
    {
        if (!Directory.Exists(_dir)) yield break;
        foreach (string path in Directory.GetFiles(_dir, "*.mrk"))
        {
            (string DraftId, string Base, MarcRecord Record)? entry = null;
            try
            {
                string id = Path.GetFileNameWithoutExtension(path);
                int us = id.IndexOf('_');
                string @base = us > 0 ? id[..us] : "BIB";
                if (@base is not ("BIB" or "AUT")) @base = "BIB";
                var read = MrkReader.Read(File.ReadAllText(path));
                if (read.Records.Count > 0) entry = (id, @base, read.Records[0]);
            }
            catch (Exception) { /* unreadable or unparseable draft file — skip it, never crash */ }
            if (entry is { } v) yield return v;
        }
    }

    /// <summary>Writes the record to its draft file, minting a new id when none is
    /// given (the id carries the base as a prefix, e.g. <c>BIB_1a2b…</c>, so
    /// <see cref="LoadAll"/> can route it without opening the file). Returns the id
    /// so the caller can re-save the same file next time.</summary>
    public string Save(string? draftId, string @base, MarcRecord record)
    {
        Directory.CreateDirectory(_dir);
        draftId ??= @base + "_" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(_dir, draftId + ".mrk"), MrkWriter.ToBytes(new[] { record }));
        return draftId;
    }

    /// <summary>Removes a draft's file — after a push commits it, or an explicit
    /// discard. A missing file is fine.</summary>
    public void Delete(string draftId)
    {
        try { File.Delete(Path.Combine(_dir, draftId + ".mrk")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // A stable per-catalogue subfolder name from the catalogue's full path (case-
    // folded on Windows). 8 bytes of SHA-256 as hex is ample to avoid collisions
    // while keeping the folder name short.
    private static string Key(string catalogPath)
    {
        string full;
        try { full = Path.GetFullPath(catalogPath); } catch { full = catalogPath; }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8);
    }
}
