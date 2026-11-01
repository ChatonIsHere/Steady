using System;
using System.Collections.Generic;
using Alta.Networking;
using Alta.Saving;
using Alta.Serialization;
using HarmonyLib;
using Stream = Alta.Serialization.Stream;

namespace Steady.Fixes;

// SavingSystem.Write serializes each component's save data twice: into a StreamCounter to learn its length
// (written first, as 32 bits), then for real. Steady serializes it once into a scratch writer that starts at
// the same bit alignment as the real stream, writes the length, then copies the words across. Same alignment
// means the same words, so the save bytes are identical. If the scratch pass throws, the game's own two passes
// run on the same save data, failing (and logging) exactly as they would have.
internal static class SaveOnce
{
    private static readonly AccessTools.FieldRef<BitWriter, ulong> Scratch = AccessTools.FieldRefAccess<BitWriter, ulong>("scratch");
    private static readonly AccessTools.FieldRef<BitWriter, int> PendingBits = AccessTools.FieldRefAccess<BitWriter, int>("bitIndex");
    private static readonly AccessTools.FieldRef<BitWriter, uint> Words = AccessTools.FieldRefAccess<BitWriter, uint>("wordIndex");
    private static NLog.Logger _log;

    // One scratch writer per nesting level, per thread.
    [ThreadStatic] private static List<StreamWriter> _scratch;
    [ThreadStatic] private static int _depth;

    public static void Apply()
    {
        // The copy relies on BitWriter storing words as-is, which it does on little-endian machines.
        if (!SteadyConfig.SinglePassSaves || !BitConverter.IsLittleEndian) return;
        _log = AccessTools.Field(typeof(SavingSystem), "logger")?.GetValue(null) as NLog.Logger;
        if (_log != null && SteadyMod.Patch(AccessTools.Method(typeof(SavingSystem), "Write"), "SinglePassSaves", typeof(SaveOnce), nameof(Write)))
            SteadyMod.Active.Add("saves serialized once");
    }

    private static bool Write(Stream stream, ISavableSaveBase savable, SavingSystem.SavingSetup setup, ISerializeChildren childrenSerializer)
    {
        if (stream is not StreamWriter real) return true;
        SaveData data;
        try
        {
            data = setup.Save(savable, childrenSerializer);
        }
        catch (Exception e)
        {
            LogFailure(e, setup);
            return false;
        }
        WriteData(real, data, setup);
        return false;
    }

    internal static void WriteData(StreamWriter real, SaveData data, SavingSystem.SavingSetup setup)
    {
        var list = _scratch ??= new List<StreamWriter>();
        if (_depth == list.Count) list.Add(new StreamWriter(Stream.ResizeDataFunc));
        var scratch = list[_depth++];
        try
        {
            scratch.Clear();
            var offset = (int)(real.BitIndex % 32);
            try
            {
                if (offset > 0) scratch.Writer.WriteBits(0, offset);
                setup.LatestSetup.Serialize(data, scratch, SavingSystem.LatestChunkVersioning);
            }
            catch
            {
                TwoPasses(real, data, setup);
                return;
            }
            try
            {
                var length = scratch.BitIndex - (uint)offset;
                real.SerializeUnsignedInt(ref length);
                Copy(scratch.Writer, offset, real.Writer);
            }
            catch (Exception e)
            {
                LogFailure(e, setup);
            }
        }
        finally
        {
            _depth--;
        }
    }

    // Copies from's bits after the first `skip` (alignment padding) onto to, which sits at the same bit offset
    // within its word. After the first partial word both are word-aligned, so whole words go over unchanged.
    private static void Copy(BitWriter from, int skip, BitWriter to)
    {
        var words = Words(from);
        var pending = PendingBits(from);
        var data = from.Data;
        if (words == 0)
        {
            if (pending > skip) to.WriteBits((uint)(Scratch(from) >> (64 - pending)), pending - skip);
            return;
        }
        to.WriteBits(data[0], 32 - skip);
        for (var i = 1; i < words; i++) to.WriteBits(data[i], 32);
        if (pending > 0) to.WriteBits((uint)(Scratch(from) >> (64 - pending)), pending);
    }

    // The game's own Write, after making the save data.
    private static void TwoPasses(StreamWriter real, SaveData data, SavingSystem.SavingSetup setup)
    {
        try
        {
            var counter = new StreamCounter(real.BitIndex);
            setup.LatestSetup.Serialize(data, counter, SavingSystem.LatestChunkVersioning);
            var length = counter.BitIndex;
            real.SerializeUnsignedInt(ref length);
            setup.LatestSetup.Serialize(data, real, SavingSystem.LatestChunkVersioning);
        }
        catch (Exception e)
        {
            LogFailure(e, setup);
        }
    }

    private static void LogFailure(Exception e, SavingSystem.SavingSetup setup)
    {
        _log.Error(e);
        _log.Error("Error saving {0}. Potential issues with save file. See Exception above.", setup.Topic.Name);
    }
}
