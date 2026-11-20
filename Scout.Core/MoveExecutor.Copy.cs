using System.Security.Cryptography;
using System.Text.Json.Nodes;
namespace Scout.Core;

public static partial class MoveExecutor {
    // Destination is published without replacement only after a full read-back
    // checksum. The original remains present until the published copy is verified.
    static async Task CopyAcrossVolumesAsync(JsonObject entry, JsonObject row, string source, string destination,
        FileStream sourceGuard, Action save, Action<string> notify, CancellationToken ct) {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".scout-" + entry.Text("id") + ".partial");
        entry["transfer_method"] = "verified-copy-v1"; entry["copy_path"] = temporary;
        save();
        bool created = false;
        try {
            notify("Copying to the destination drive: " + Path.GetFileName(source));
            using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)) {
                created = true; sourceGuard.Position = 0; byte[] buffer = new byte[1024 * 1024];
                int read;
                while ((read = await sourceGuard.ReadAsync(buffer, ct)) != 0) {
                    checksum.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await output.FlushAsync(ct); output.Flush(flushToDisk: true);
            }
            var expected = Convert.ToHexStringLower(checksum.GetHashAndReset());
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            notify("Verifying the complete destination copy: " + Path.GetFileName(source));
            if (new FileInfo(temporary).Length != sourceGuard.Length || await Hash(temporary, ct) != expected)
                throw new IOException("Destination copy verification failed. The source is unchanged.");
            if (!RenameVerification.Matches(source, entry["verification"]!, requireIdentity: false) ||
                entry["verification"].Text("file_id") == "" && await Hash(source, ct) != expected)
                throw new IOException("Source changed while copying. It was not removed.");
            entry["sha256"] = expected; entry["copy_length"] = sourceGuard.Length;
            save(); ct.ThrowIfCancellationRequested();
            Paths(entry.Text("source_root"), entry.Text("destination_root"), source, destination);
            RenameVerification.Move(temporary, destination); entry["copy_published"] = true;
            // Do not act on cancellation after publication: finish this verified
            // move, just as a same-volume rename completes its current file.
            using var destinationGuard = RenameVerification.OpenGuard(destination);
            if (await Hash(destination, CancellationToken.None) != expected)
                throw new IOException("Published destination changed. Both locations are held for review.");
            entry["destination_verification"] = RenameVerification.Capture(destination, destinationGuard.SafeFileHandle, row["primary_fingerprint"]!);
            save();
            Paths(entry.Text("source_root"), entry.Text("destination_root"), source, destination);
            if (!RenameVerification.Matches(source, entry["verification"]!, requireIdentity: false))
                throw new IOException("Source identity changed before removal. Both locations are held for review.");
            File.Delete(source);
        } finally {
            // This name was created exclusively for this journal entry. Never
            // remove a destination, source, or a pre-existing temporary file.
            if (created && File.Exists(temporary) && !entry.Bool("copy_published") && !PreviewPlanner.HasReparseAncestor(temporary)) File.Delete(temporary);
        }
    }
}
