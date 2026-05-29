namespace DZ;

using System.Buffers;
using System.Numerics;
using System.Runtime.Intrinsics;

/// <summary>
/// Handles delta encoding/compression operations.
/// </summary>
public static class DeltaEncoder
{
    // Encoder-specific methods will be moved here:
    // - CreateRLEDelta
    // - EncodeXorWithMotifs
    // - FindMotifCandidate
    // - CheckUniform
    // Dependencies: DeltaUtils for SIMD, 7-bit encoding, constants

    // Advanced motif detection structures
    private struct MotifAccumulator
    {
        public int UnitSize;
        public uint Mask;
        public int Streak;
        public bool IsUniform;
        public bool IsFull;
        public int ChangedCount;
        public float Density;
        public int StartPos;
        public bool Active;

        public void Reset()
        {
            Active = false;
            Streak = 0;
            StartPos = 0;
        }

        public bool TryStart(ReadOnlySpan<byte> xorData, int pos, int maxUnitSize = 8)
        {
            if (pos + maxUnitSize > xorData.Length) return false;

            // Probe unit sizes 2-8
            for (int u = 2; u <= maxUnitSize; u++)
            {
                uint mask = 0;
                int pop = 0;
                for (int i = 0; i < u; i++)
                {
                    if (xorData[pos + i] != 0)
                    {
                        mask |= (1u << i);
                        pop++;
                    }
                }
                if (pop == 0) continue;

                bool isFull = (pop == u);
                float density = pop / (float)u;
                if (!isFull && density >= 0.7f) continue; // Prune high density

                // Check if at least one repeat possible
                if (pos + 2 * u > xorData.Length) continue;

                // Check pattern consistency for first repeat
                bool matches = true;
                if (isFull)
                {
                    matches = xorData.Slice(pos, u).SequenceEqual(xorData.Slice(pos + u, u));
                }
                else
                {
                    for (int i = 0; i < u; i++)
                    {
                        byte val = xorData[pos + u + i];
                        if ((mask & (1u << i)) != 0)
                        {
                            if (val == 0) { matches = false; break; }
                        }
                        else
                        {
                            if (val != 0) { matches = false; break; }
                        }
                    }
                }
                if (!matches) continue;

                // Check uniformity
                bool uniform = isFull || CheckUniform(xorData, pos, u, mask, 2);

                UnitSize = u;
                Mask = mask;
                Streak = 2;
                IsUniform = uniform;
                IsFull = isFull;
                ChangedCount = pop;
                Density = density;
                StartPos = pos;
                Active = true;
                return true;
            }
            return false;
        }

        public bool TryExtend(ReadOnlySpan<byte> xorData, int currentPos)
        {
            if (!Active) return false;

            int nextStart = StartPos + Streak * UnitSize;
            if (nextStart + UnitSize > xorData.Length) return false;

            // Check if next unit matches the pattern
            if (IsFull && IsUniform)
            {
                // For full uniform, check if equals first unit
                if (!xorData.Slice(nextStart, UnitSize).SequenceEqual(xorData.Slice(StartPos, UnitSize)))
                    return false;
            }
            else
            {
                for (int i = 0; i < UnitSize; i++)
                {
                    byte val = xorData[nextStart + i];
                    if ((Mask & (1u << i)) != 0)
                    {
                        if (val == 0) return false;
                    }
                    else
                    {
                        if (val != 0) return false;
                    }
                }

                // If not uniform, check values match
                if (!IsUniform && !IsFull)
                {
                    if (!CheckUniform(xorData, StartPos, UnitSize, Mask, Streak + 1))
                        return false;
                }
            }

            Streak++;
            return true;
        }

        public int CoveredLength => Active ? Streak * UnitSize : 0;

        public bool ShouldEmit(ReadOnlySpan<byte> xorData, float savingsThreshold = -0.1f)
        {
            if (!Active || Streak < 2) return false;

            int covered = CoveredLength;
            int headerSize = 1 + 1 + DeltaUtils.Get7BitEncodedSize(Streak) + DeltaUtils.Get7BitEncodedSize(UnitSize);
            int dataSize = ChangedCount * (IsUniform ? 1 : Streak);
            int motifSize = IsFull ? headerSize + dataSize : headerSize + DeltaUtils.Get7BitEncodedSize((int)Mask) + dataSize;

            int rleSize = DeltaUtils.EstimateRLESizeForSpan(xorData.Slice(StartPos, covered));
            float savings = (rleSize - motifSize) / (float)rleSize;
            return savings > savingsThreshold;
        }
    }

    private static bool CheckUniform(ReadOnlySpan<byte> xorData, int start, int unit, uint msk, int reps)
    {
        int popc = BitOperations.PopCount(msk);
        Span<byte> first = stackalloc byte[popc];
        int idx = 0;
        for (int i = 0; i < unit; i++)
        {
            if ((msk & (1u << i)) != 0)
                first[idx++] = xorData[start + i];
        }

        for (int r = 1; r < reps; r++)
        {
            idx = 0;
            for (int i = 0; i < unit; i++)
            {
                if ((msk & (1u << i)) != 0)
                {
                    if (xorData[start + r * unit + i] != first[idx])
                        return false;
                    idx++;
                }
            }
        }

        return true;
    }

    private static DeltaUtils.MotifCandidate? FindMotifCandidate(ReadOnlySpan<byte> xorData, int startPos, DeltaZor.DeltaOptions options)
    {
        int len = xorData.Length - startPos;
        Span<byte> firstUnitBuffer = stackalloc byte[8];
        for (int u = 0; u < DeltaUtils.MotifProbeCount; u++)
        {
            int unitSize = DeltaUtils.MotifUnitSizes[u];
            int maxPossibleRepeat = len / unitSize;
            if (maxPossibleRepeat < DeltaUtils.MotifMinStreak) continue;

            // Compute mask from first unit
            uint mask = 0;
            int pop = 0;
            for (int i = 0; i < unitSize; i++)
            {
                if (xorData[startPos + i] != 0)
                {
                    mask |= (1u << i);
                    pop++;
                }
            }

            if (pop == 0) continue;

            bool isFull = (pop == unitSize);
            float density = pop / (float)unitSize;
            if (!isFull && density >= DeltaUtils.MotifDensityThreshold) continue; // Prune high density masked mode only

            int repeatLen;
            bool isUniform;

            if (isFull)
            {
                // Full mode: check for uniform repeats

                xorData.Slice(startPos, unitSize).CopyTo(firstUnitBuffer[..unitSize]);
                // // if (density >= MotifDensityThreshold) continue; // Allow emission for full mode high density repeats
                repeatLen = 1;
                for (int r = 1; r < maxPossibleRepeat; r++)
                {
                    if (!xorData.Slice(startPos + r * unitSize, unitSize).SequenceEqual(firstUnitBuffer[..unitSize]))
                        break;
                    repeatLen++;
                }

                repeatLen = Math.Min(repeatLen, DeltaUtils.MaxMotifStreak);
                isUniform = true;
            }
            else
            {
                // Masked mode: check pattern consistency
                repeatLen = 1;
                for (int r = 1; r < maxPossibleRepeat; r++)
                {
                    bool matches = true;
                    for (int i = 0; i < unitSize; i++)
                    {
                        byte val = xorData[startPos + r * unitSize + i];
                        if ((mask & (1u << i)) != 0)
                        {
                            if (val == 0)
                            {
                                matches = false;
                                break;
                            }
                        }
                        else
                        {
                            if (val != 0)
                            {
                                matches = false;
                                break;
                            }
                        }
                    }

                    if (!matches) break;
                    repeatLen++;
                }

                repeatLen = Math.Min(repeatLen, DeltaUtils.MaxMotifStreak);
                isUniform = CheckUniform(xorData, startPos, unitSize, mask, repeatLen);
            }

            if (repeatLen > DeltaUtils.MaxMotifStreak) continue;
            if (repeatLen < DeltaUtils.MotifMinStreak) continue;

            int covered = repeatLen * unitSize;
            int headerSize = 1 + 1 + DeltaUtils.Get7BitEncodedSize(repeatLen) + DeltaUtils.Get7BitEncodedSize(unitSize);
            int changedCount = isFull ? unitSize : pop;
            int dataSize = changedCount * (isUniform ? 1 : repeatLen);
            int motifSize = isFull ? headerSize + dataSize : headerSize + DeltaUtils.Get7BitEncodedSize((int)mask) + dataSize;

            int rleSize = DeltaUtils.EstimateRLESizeForSpan(xorData.Slice(startPos, covered));
            float savings = (rleSize - motifSize) / (float)rleSize;

            if (savings > DeltaUtils.MotifSavingsThreshold)
            {
                return new DeltaUtils.MotifCandidate(unitSize, repeatLen, covered, isFull ? 0u : mask, isUniform, isFull);
            }
        }

        return null;
    }

    private static DeltaZor.OpCodeCounts EncodeXorWithMotifs(ReadOnlySpan<byte> xorData, IBufferWriter<byte> writer,
        DeltaZor.DeltaOptions options, DeltaZor.OpCodeCounts counts)
    {
        int pos = 0;
        Span<byte> oneByteSpan = stackalloc byte[1];
        Span<byte> tempBuffer = stackalloc byte[options.MaxStackBufferSize];
        Span<int> posListBuffer = stackalloc int[32];
        MotifAccumulator accumulator = new();
        accumulator.Reset();

        while (pos < xorData.Length)
        {
            // Try to extend current motif
            if (accumulator.Active && accumulator.TryExtend(xorData, pos))
            {
                // Extended, continue
                pos += accumulator.UnitSize;
                continue;
            }

            // Check if current motif should be emitted
            if (accumulator.Active && accumulator.ShouldEmit(xorData))
            {
                // Emit motif
                EmitMotif(accumulator, xorData, writer, tempBuffer, posListBuffer, ref counts);
                pos = accumulator.StartPos + accumulator.CoveredLength;
                accumulator.Reset();
                continue;
            }

            // Reset accumulator if not extended or not emitting
            if (accumulator.Active)
            {
                accumulator.Reset();
            }

            // Try to start new motif
            if (accumulator.TryStart(xorData, pos))
            {
                pos += accumulator.UnitSize;
                continue;
            }

            // Try a FloatRun (0x06): float32-lane sparse run that motifs (unit cap 8)
            // cannot reach. Only at a 4-aligned position and only when strictly smaller
            // than the equivalent byte-RLE. See Encoder.cs source-of-truth (mirrored by
            // Zig encoder.zig tryEmitFloatRun).
            if (TryEmitFloatRun(xorData, pos, writer, tempBuffer, ref counts, out int floatCovered))
            {
                pos += floatCovered;
                continue;
            }

            // Fallback to RLE run
            bool isZero = xorData[pos] == 0;
            int runLen = 1;
            while (pos + runLen < xorData.Length && (xorData[pos + runLen] == 0) == isZero) runLen++;
            byte op = isZero ? DeltaUtils.RLE_ZeroRun : DeltaUtils.RLE_NonZeroRun;
            oneByteSpan[0] = op;
            writer.Write(oneByteSpan);
            DeltaUtils.Write7BitEncodedInt(writer, runLen);
            if (!isZero)
            {
                Span<byte> xorTemp = tempBuffer[..runLen];
                xorData.Slice(pos, runLen).CopyTo(xorTemp);
                writer.Write(xorTemp);
            }

            if (isZero) counts.ZeroRunCount++;
            else counts.NonZeroRunCount++;
            pos += runLen;
        }

        // Emit any remaining motif
        if (accumulator.Active && accumulator.ShouldEmit(xorData))
        {
            EmitMotif(accumulator, xorData, writer, tempBuffer, posListBuffer, ref counts);
        }

        return counts;
    }

    private static void EmitMotif(MotifAccumulator acc, ReadOnlySpan<byte> xorData, IBufferWriter<byte> writer,
        Span<byte> tempBuffer, Span<int> posListBuffer, ref DeltaZor.OpCodeCounts counts)
    {
        Span<byte> oneByteSpan = stackalloc byte[1];
        bool isUniform = acc.IsUniform;
        bool isFull = acc.IsFull;
        uint mask = acc.Mask;
        int unitSize = acc.UnitSize;
        int repeatLength = acc.Streak;
        int changedCount = acc.ChangedCount;
        byte opcode = isUniform ? DeltaUtils.RLE_UniformMotifRepeat : DeltaUtils.RLE_VaryingMotifRepeat;
        oneByteSpan[0] = opcode;
        writer.Write(oneByteSpan);
        byte flags = (byte)(isFull ? 0x00 : 0x80);
        oneByteSpan[0] = flags;
        writer.Write(oneByteSpan);
        DeltaUtils.Write7BitEncodedInt(writer, repeatLength);
        DeltaUtils.Write7BitEncodedInt(writer, unitSize);
        if (!isFull)
            DeltaUtils.Write7BitEncodedInt(writer, (int)mask);

        int dataLen = changedCount * (isUniform ? 1 : repeatLength);
        Span<byte> packed = tempBuffer[..dataLen];
        if (isFull)
        {
            xorData.Slice(acc.StartPos, dataLen).CopyTo(packed);
        }
        else
        {
            Span<int> posList = posListBuffer[..changedCount];
            int pp = 0;
            for (int ii = 0; ii < unitSize; ii++)
                if ((mask & (1u << ii)) != 0)
                    posList[pp++] = ii;
            int cursor = 0;
            int maxRr = isUniform ? 1 : repeatLength;
            for (int rr = 0; rr < maxRr; rr++)
            for (int jj = 0; jj < changedCount; jj++)
                packed[cursor++] = xorData[acc.StartPos + rr * unitSize + posList[jj]];
        }

        writer.Write(packed);

        float density = acc.Density;
        int totalMotif = counts.UniformMotifCount + counts.VaryingMotifCount + 1;
        float newAvg = (counts.AverageMaskDensity * (totalMotif - 1) + density) / totalMotif;
        if (isUniform)
        {
            counts.UniformMotifCount++;
            counts.AverageMaskDensity = newAvg;
        }
        else
        {
            counts.VaryingMotifCount++;
            counts.AverageMaskDensity = newAvg;
        }
    }

    private static bool LaneChanged(ReadOnlySpan<byte> xorData, int baseOff) =>
        xorData[baseOff] != 0 || xorData[baseOff + 1] != 0 ||
        xorData[baseOff + 2] != 0 || xorData[baseOff + 3] != 0;

    // Estimates the byte cost the existing motif + byte-RLE pipeline would produce for a span
    // of the XOR stream, WITHOUT the FloatRun probe (to avoid recursion and to measure the
    // genuine alternative FloatRun must beat). This is the live MotifAccumulator state machine
    // (TryExtend/ShouldEmit/EmitMotif sizing + ZeroRun/NonZeroRun fallback) run as a pure
    // size counter — it never writes bytes. FloatRun emits ONLY when strictly smaller than
    // this, so a mid-span motif-able block (which this counter prices cheaply) blocks the
    // FloatRun. Mirrored byte-for-byte by Zig encoder.zig estimateMotifRleSizeForSpan.
    // (TASK-0361 codex REJECT B fix: gate vs the motif/RLE alternative, not just byte-RLE.)
    private static int EstimateMotifRleSizeForSpan(ReadOnlySpan<byte> span)
    {
        int size = 0;
        int pos = 0;
        MotifAccumulator acc = new();
        acc.Reset();

        while (pos < span.Length)
        {
            if (acc.Active && acc.TryExtend(span, pos))
            {
                pos += acc.UnitSize;
                continue;
            }

            if (acc.Active && acc.ShouldEmit(span))
            {
                size += MotifEmitSize(acc);
                pos = acc.StartPos + acc.CoveredLength;
                acc.Reset();
                continue;
            }

            if (acc.Active)
                acc.Reset();

            if (acc.TryStart(span, pos))
            {
                pos += acc.UnitSize;
                continue;
            }

            // Basic RLE run fallback (matches EncodeXorWithMotifs).
            bool isZero = span[pos] == 0;
            int runLen = 1;
            while (pos + runLen < span.Length && (span[pos + runLen] == 0) == isZero) runLen++;
            size += 1 + DeltaUtils.Get7BitEncodedSize(runLen) + (isZero ? 0 : runLen);
            pos += runLen;
        }

        // Trailing motif (matches EncodeXorWithMotifs end-of-loop emit).
        if (acc.Active && acc.ShouldEmit(span))
            size += MotifEmitSize(acc);

        return size;
    }

    // Emitted byte size of a motif (mirrors EmitMotif framing). Source of truth for the
    // FloatRun motif-alternative gate; mirrored by Zig encoder.zig motifEmitSize.
    private static int MotifEmitSize(in MotifAccumulator acc)
    {
        int headerSize = 1 + 1 + DeltaUtils.Get7BitEncodedSize(acc.Streak) + DeltaUtils.Get7BitEncodedSize(acc.UnitSize);
        int dataLen = acc.ChangedCount * (acc.IsUniform ? 1 : acc.Streak);
        return acc.IsFull ? headerSize + dataLen : headerSize + DeltaUtils.Get7BitEncodedSize((int)acc.Mask) + dataLen;
    }

    // FloatRun 0x06 probe + emit. SINGLE SOURCE OF TRUTH for the float-lane opcode;
    // mirrored byte-for-byte by Zig encoder.zig tryEmitFloatRun. Framing:
    //   [0x06][flags=0x00][laneCount:7bit][laneBitmap: ceil(laneCount/8)][packedXor: 4*changedLanes]
    // Treats the XOR stream as float32 lanes (4-byte units aligned to the stream origin).
    // Targets SPARSE/STRIDED changed lanes that motifs (unit cap 8) cannot reach. To avoid
    // swallowing a downstream motif-able block, the run:
    //   (a) requires the FIRST lane at pos to be changed (no leading zero lanes — those are
    //       left for the natural ZeroRun, which re-probes FloatRun at the next changed lane);
    //   (b) trims trailing zero lanes (laneCount = lastChangedLane + 1);
    // and is MOTIF-AWARE: it emits ONLY when floatSize is strictly smaller than BOTH byte-RLE
    // AND the actual motif/RLE encoder cost over the same span (EstimateMotifRleSizeForSpan).
    // A mid-span motif-able block (e.g. a long Uniform repeat after a changed first lane) is
    // priced cheaply by the motif estimate, so the gate rejects and the region is left to the
    // motif/RLE path — FloatRun never regresses size vs the existing pipeline. A genuine
    // strided/sparse float pattern that motifs cannot capture still wins (Test046).
    // (TASK-0361 codex REJECT B fix: gate vs the motif alternative, not just byte-RLE alone.)
    private static bool TryEmitFloatRun(ReadOnlySpan<byte> xorData, int pos, IBufferWriter<byte> writer,
        Span<byte> tempBuffer, ref DeltaZor.OpCodeCounts counts, out int covered)
    {
        covered = 0;
        const int LaneSize = 4;
        if ((pos & 3) != 0) return false; // require 4-aligned lane start
        int avail = xorData.Length - pos;
        int maxLanes = avail / LaneSize;
        if (maxLanes < 2) return false; // need at least 2 lanes
        if (!LaneChanged(xorData, pos)) return false; // (a) no leading zero lanes

        // Count changed lanes and find the last changed lane (to trim trailing zeros).
        int changedLanes = 0;
        int lastChanged = -1;
        for (int l = 0; l < maxLanes; l++)
        {
            if (LaneChanged(xorData, pos + l * LaneSize))
            {
                changedLanes++;
                lastChanged = l;
            }
        }

        int laneCount = lastChanged + 1; // (b) trim trailing zero lanes
        if (laneCount < 2) return false;
        int span = laneCount * LaneSize;

        int bitmapBytes = (laneCount + 7) / 8;
        int floatSize = 1 + 1 + DeltaUtils.Get7BitEncodedSize(laneCount) + bitmapBytes + LaneSize * changedLanes;
        ReadOnlySpan<byte> spanSlice = xorData.Slice(pos, span);
        int rleSize = DeltaUtils.EstimateRLESizeForSpan(spanSlice);
        if (floatSize >= rleSize) return false; // strict improvement vs byte-RLE
        // Motif-aware gate: also beat the actual motif/RLE encoder cost over the span, so a
        // mid-span motif-able block cannot be swallowed at a net regression.
        int motifRleSize = EstimateMotifRleSizeForSpan(spanSlice);
        if (floatSize >= motifRleSize) return false; // strict improvement vs motif/RLE too

        // Emit opcode + flags + laneCount.
        Span<byte> oneByteSpan = stackalloc byte[1];
        oneByteSpan[0] = DeltaUtils.RLE_FloatRun;
        writer.Write(oneByteSpan);
        oneByteSpan[0] = 0x00; // flags reserved
        writer.Write(oneByteSpan);
        DeltaUtils.Write7BitEncodedInt(writer, laneCount);

        // Build and write the lane bitmap (LSB-first per byte).
        Span<byte> bitmap = bitmapBytes <= 512 ? stackalloc byte[bitmapBytes] : new byte[bitmapBytes];
        bitmap.Clear();
        for (int l = 0; l < laneCount; l++)
        {
            if (LaneChanged(xorData, pos + l * LaneSize))
                bitmap[l >> 3] |= (byte)(1 << (l & 7));
        }
        writer.Write(bitmap);

        // Pack the 4 XOR bytes of each changed lane, in lane order.
        int dataLen = LaneSize * changedLanes;
        Span<byte> packed = dataLen <= tempBuffer.Length ? tempBuffer[..dataLen] : new byte[dataLen];
        int cursor = 0;
        for (int l = 0; l < laneCount; l++)
        {
            int baseOff = pos + l * LaneSize;
            if (LaneChanged(xorData, baseOff))
            {
                xorData.Slice(baseOff, LaneSize).CopyTo(packed.Slice(cursor, LaneSize));
                cursor += LaneSize;
            }
        }
        writer.Write(packed);

        counts.FloatPatternCount++;
        covered = span;
        return true;
    }

    public static DeltaZor.OpCodeCounts CreateRLEDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData,
        IBufferWriter<byte> writer, DeltaZor.DeltaOptions options)
    {
        var patternCounts = new DeltaZor.OpCodeCounts();
        int minLength = Math.Min(oldData.Length, newData.Length);
        Span<byte> oneByteSpan = stackalloc byte[1];
        Span<byte> tempBuffer = stackalloc byte[options.MaxStackBufferSize];

        bool useFullXor = minLength <= options.MaxStackBufferSize && options.EnableMotifDetection;
        if (useFullXor)
        {
            Span<byte> xorBuffer = stackalloc byte[minLength];
            DeltaUtils.WriteXORDelta(oldData, newData, xorBuffer, 0, minLength, options);
            patternCounts = EncodeXorWithMotifs(xorBuffer, writer, options, patternCounts);
        }
        else
        {
            // Streaming RLE without motifs for large data
            int pos = 0;
            while (pos < minLength)
            {
                int runStart = pos;
                bool isZeroRun = oldData[pos] == newData[pos];
                while (pos < minLength && (oldData[pos] == newData[pos]) == isZeroRun)
                    pos++;
                int runLen = pos - runStart;
                byte opcode = isZeroRun ? DeltaUtils.RLE_ZeroRun : DeltaUtils.RLE_NonZeroRun;
                oneByteSpan[0] = opcode;
                writer.Write(oneByteSpan);
                DeltaUtils.Write7BitEncodedInt(writer, runLen);
                if (!isZeroRun)
                {
                    Span<byte> xorTemp = tempBuffer[..runLen];
                    DeltaUtils.WriteXORDelta(oldData, newData, xorTemp, runStart, runLen, options);
                    writer.Write(xorTemp);
                }

                if (isZeroRun) patternCounts.ZeroRunCount++;
                else patternCounts.NonZeroRunCount++;
            }
        }

        // Handle length differences
        if (newData.Length > oldData.Length)
        {
            ReadOnlySpan<byte> extension = newData[oldData.Length..];
            oneByteSpan[0] = DeltaUtils.RLE_Extension;
            writer.Write(oneByteSpan);
            DeltaUtils.Write7BitEncodedInt(writer, extension.Length);
            writer.Write(extension);
            patternCounts = patternCounts with { ExtensionCount = patternCounts.ExtensionCount + 1 };
        }
        else if (newData.Length < oldData.Length)
        {
            oneByteSpan[0] = DeltaUtils.RLE_Truncation;
            writer.Write(oneByteSpan);
            DeltaUtils.Write7BitEncodedInt(writer, newData.Length);
            patternCounts = patternCounts with { TruncationCount = patternCounts.TruncationCount + 1 };
        }

        return patternCounts;
    }
}