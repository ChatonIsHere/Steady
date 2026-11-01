using System;
using System.IO;
using System.Threading;
using Alta.Utilities;

namespace Steady.Fixes;

// Every autosave rewrites the file of every loaded chunk, and about half come out byte-for-byte the same as the
// file already on disk apart from the save date (which the game writes but never reads). For those, Steady
// abandons the write the way AltaFile abandons a cancelled one: its temp file is deleted, and the chunk file and
// its backup stay as they are. It compares against the file on disk, never a remembered copy.
internal static class UnchangedChunks
{
    private const int DateBytes = 12;

    internal static int Skipped;

    public static void Apply()
    {
        if (!SteadyConfig.SkipUnchangedChunks) return;
        if (SteadyMod.Patch(HarmonyLib.AccessTools.Method(typeof(ChunkSaveFormat), nameof(ChunkSaveFormat.WriteTo)), "SkipUnchangedChunks", typeof(UnchangedChunks), nameof(WriteTo)))
            SteadyMod.Active.Add("unchanged chunk files skipped");
    }

    // Off the main thread. AltaFile writes into name_temp.ext, then swaps it in for name.ext.
    private static bool WriteTo(ChunkSaveFormat __instance, System.IO.Stream stream, CancellationToken token)
    {
        if (stream is not FileStream temp || __instance.Versions.Count != 1) return true;
        var name = Path.GetFileNameWithoutExtension(temp.Name);
        if (!name.EndsWith("_temp", StringComparison.Ordinal)) return true;
        var target = Path.Combine(Path.GetDirectoryName(temp.Name), name.Substring(0, name.Length - 5) + Path.GetExtension(temp.Name));
        byte[] bytes;
        using (var memory = new MemoryStream())
        {
            __instance.WriteTo(memory, token);
            bytes = memory.ToArray();
        }
        if (SameApartFromDate(bytes, target))
        {
            Interlocked.Increment(ref Skipped);
            throw new OperationCanceledException();
        }
        temp.Write(bytes, 0, bytes.Length);
        return false;
    }

    // With one version, the file ends with the date and then a "more versions follow" byte.
    private static bool SameApartFromDate(byte[] bytes, string path)
    {
        byte[] old;
        try
        {
            old = File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            return false;
        }
        if (old.Length != bytes.Length || bytes.Length < DateBytes + 5) return false;
        int dateStart = bytes.Length - 1 - DateBytes, dateEnd = bytes.Length - 1;
        for (var i = 0; i < bytes.Length; i++)
            if ((i < dateStart || i >= dateEnd) && old[i] != bytes[i]) return false;
        return true;
    }
}
