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

            // Try a ChannelRun (0x08) FIRST: channel-interleaved byte run at a stride GREATER than
            // the motif unit cap (8) — the gap motif cannot reach — where a fixed small set of byte
            // channels changes per unit. Probed BEFORE HalfRun/FloatRun because it carries the most
            // complete gate: it emits ONLY when strictly smaller than byte-RLE, motif/RLE, FloatRun
            // AND HalfRun over the span. So it pre-empts Half/Float only when genuinely cheaper, and
            // otherwise declines (channelSize >= the cheaper alternative), letting Half/Float probe
            // next — deterministic coexistence with no double-fire and no regression. See
            // TryEmitChannelRun (source of truth; mirrored by Zig encoder.zig tryEmitChannelRun).
            // TASK-0363 / EPIC-0045.
            if (TryEmitChannelRun(xorData, pos, writer, tempBuffer, ref counts, out int channelCovered))
            {
                pos += channelCovered;
                continue;
            }

            // Try a HalfRun (0x07): float16 (2-byte) lane sparse run, probed BEFORE FloatRun
            // (0x06). Its motif-aware gate also beats the FloatRun alternative, so it only
            // fires on genuinely 2-byte-granular shapes and yields (declines) to FloatRun on
            // 4-byte-dense shapes. See TryEmitHalfRun (source of truth; mirrored by Zig
            // encoder.zig tryEmitHalfRun). TASK-0362 / EPIC-0045.
            if (TryEmitHalfRun(xorData, pos, writer, tempBuffer, ref counts, out int halfCovered))
            {
                pos += halfCovered;
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

    // Pure size counter: the byte cost a HalfRun (0x07) would emit over a span anchored at
    // stream position `pos` (the same span a candidate ChannelRun covers). Returns int.MaxValue
    // ("infeasible") when HalfRun cannot represent the span identically — i.e. when `pos` is not
    // 2-aligned, the span length is not a multiple of 2, the span holds < 2 float16 lanes, or the
    // first half-lane is not changed (HalfRun requires a changed first lane). This is the HalfRun
    // term of the ChannelRun gate so a ChannelRun never undercuts (steals) a position HalfRun
    // would encode at least as cheaply. Never writes bytes. Mirrors EstimateFloatRunSizeForSpan
    // exactly at 2-byte granularity; mirrored byte-for-byte by Zig encoder.zig
    // estimateHalfRunSizeForSpan. (TASK-0363.)
    private static int EstimateHalfRunSizeForSpan(ReadOnlySpan<byte> span, int pos)
    {
        const int LaneSize = 2;
        if ((pos & 1) != 0) return int.MaxValue;          // HalfRun needs a 2-aligned start
        if ((span.Length & 1) != 0) return int.MaxValue;  // HalfRun covers whole 2B lanes
        int maxLanes = span.Length / LaneSize;
        if (maxLanes < 2) return int.MaxValue;            // HalfRun needs >= 2 lanes
        if (!HalfLaneChanged(span, 0)) return int.MaxValue; // HalfRun requires first lane changed

        int changedLanes = 0;
        int lastChanged = -1;
        for (int l = 0; l < maxLanes; l++)
        {
            if (HalfLaneChanged(span, l * LaneSize))
            {
                changedLanes++;
                lastChanged = l;
            }
        }

        int laneCount = lastChanged + 1; // HalfRun trims trailing zero lanes
        if (laneCount < 2) return int.MaxValue;
        int bitmapBytes = (laneCount + 7) / 8;
        return 1 + 1 + DeltaUtils.Get7BitEncodedSize(laneCount) + bitmapBytes + LaneSize * changedLanes;
    }

    private static bool HalfLaneChanged(ReadOnlySpan<byte> xorData, int baseOff) =>
        xorData[baseOff] != 0 || xorData[baseOff + 1] != 0;

    // Pure size counter: the byte cost a FloatRun (0x06) would emit over a span that starts
    // at stream position `pos` and runs `span.Length` bytes (the same span a candidate
    // HalfRun covers). Returns int.MaxValue ("infeasible") when FloatRun cannot represent the
    // span identically — i.e. when `pos` is not 4-aligned, the span length is not a multiple
    // of 4, the span holds < 2 float32 lanes, or the first float lane is not changed (FloatRun
    // requires a changed first lane). This is the FloatRun term of the HalfRun gate so a
    // HalfRun never undercuts (steals) a position FloatRun would encode more cheaply. Never
    // writes bytes. Mirrored byte-for-byte by Zig encoder.zig estimateFloatRunSizeForSpan.
    private static int EstimateFloatRunSizeForSpan(ReadOnlySpan<byte> span, int pos)
    {
        const int LaneSize = 4;
        if ((pos & 3) != 0) return int.MaxValue;          // FloatRun needs a 4-aligned start
        if ((span.Length & 3) != 0) return int.MaxValue;  // FloatRun covers whole 4B lanes
        int maxLanes = span.Length / LaneSize;
        if (maxLanes < 2) return int.MaxValue;            // FloatRun needs >= 2 lanes
        if (!LaneChanged(span, 0)) return int.MaxValue;   // FloatRun requires first lane changed

        int changedLanes = 0;
        int lastChanged = -1;
        for (int l = 0; l < maxLanes; l++)
        {
            if (LaneChanged(span, l * LaneSize))
            {
                changedLanes++;
                lastChanged = l;
            }
        }

        int laneCount = lastChanged + 1; // FloatRun trims trailing zero lanes
        if (laneCount < 2) return int.MaxValue;
        int bitmapBytes = (laneCount + 7) / 8;
        return 1 + 1 + DeltaUtils.Get7BitEncodedSize(laneCount) + bitmapBytes + LaneSize * changedLanes;
    }

    // HalfRun 0x07 probe + emit. SINGLE SOURCE OF TRUTH for the half-float (float16) opcode;
    // mirrored byte-for-byte by Zig encoder.zig tryEmitHalfRun. Framing:
    //   [0x07][flags=0x00][laneCount:7bit][laneBitmap: ceil(laneCount/8)][packedXor: 2*changedLanes]
    // Treats the XOR stream as float16 lanes (2-byte units aligned to the stream origin).
    // Probed BEFORE FloatRun (0x06) in the basic-RLE fallback branch, at 2-aligned positions
    // only. Targets SPARSE/STRIDED 2-byte-granular changed lanes that motifs (unit cap 8),
    // byte-RLE, AND FloatRun (4-byte lanes) all encode poorly. Same first-lane-changed /
    // trailing-zero-trim discipline as FloatRun, at 2-byte granularity:
    //   (a) requires the FIRST half-lane at pos to be changed (leading zero lanes left to the
    //       natural ZeroRun, which re-probes at the next changed lane);
    //   (b) trims trailing zero lanes (laneCount = lastChangedLane + 1).
    // MOTIF-AWARE + FloatRun-aware gate (TASK-0361 lesson, extended): emits ONLY when halfSize
    // is strictly smaller than ALL of byte-RLE (EstimateRLESizeForSpan), the live motif/RLE
    // encoder cost (EstimateMotifRleSizeForSpan), AND the FloatRun alternative
    // (EstimateFloatRunSizeForSpan) over the same span. Strict improvement or no-op — never a
    // regression, and never steals a position FloatRun/motif/RLE would encode at least as well
    // (a 4-byte-dense shape has halfSize > floatSize so HalfRun declines and FloatRun fires).
    private static bool TryEmitHalfRun(ReadOnlySpan<byte> xorData, int pos, IBufferWriter<byte> writer,
        Span<byte> tempBuffer, ref DeltaZor.OpCodeCounts counts, out int covered)
    {
        covered = 0;
        const int LaneSize = 2;
        if ((pos & 1) != 0) return false; // require 2-aligned lane start
        int avail = xorData.Length - pos;
        int maxLanes = avail / LaneSize;
        if (maxLanes < 2) return false; // need at least 2 lanes
        if (!HalfLaneChanged(xorData, pos)) return false; // (a) no leading zero lanes

        // Count changed lanes and find the last changed lane (to trim trailing zeros).
        int changedLanes = 0;
        int lastChanged = -1;
        for (int l = 0; l < maxLanes; l++)
        {
            if (HalfLaneChanged(xorData, pos + l * LaneSize))
            {
                changedLanes++;
                lastChanged = l;
            }
        }

        int laneCount = lastChanged + 1; // (b) trim trailing zero lanes
        if (laneCount < 2) return false;
        int span = laneCount * LaneSize;

        int bitmapBytes = (laneCount + 7) / 8;
        int halfSize = 1 + 1 + DeltaUtils.Get7BitEncodedSize(laneCount) + bitmapBytes + LaneSize * changedLanes;
        ReadOnlySpan<byte> spanSlice = xorData.Slice(pos, span);
        int rleSize = DeltaUtils.EstimateRLESizeForSpan(spanSlice);
        if (halfSize >= rleSize) return false; // strict improvement vs byte-RLE
        int motifRleSize = EstimateMotifRleSizeForSpan(spanSlice);
        if (halfSize >= motifRleSize) return false; // strict improvement vs motif/RLE
        // FloatRun-aware gate: also beat what a FloatRun (0x06) would emit over the same span,
        // so HalfRun yields to FloatRun on 4-byte-dense shapes (no double-fire, no regression).
        int floatSize = EstimateFloatRunSizeForSpan(spanSlice, pos);
        if (halfSize >= floatSize) return false; // strict improvement vs FloatRun too

        // Emit opcode + flags + laneCount.
        Span<byte> oneByteSpan = stackalloc byte[1];
        oneByteSpan[0] = DeltaUtils.RLE_HalfRun;
        writer.Write(oneByteSpan);
        oneByteSpan[0] = 0x00; // flags reserved
        writer.Write(oneByteSpan);
        DeltaUtils.Write7BitEncodedInt(writer, laneCount);

        // Build and write the lane bitmap (LSB-first per byte).
        Span<byte> bitmap = bitmapBytes <= 512 ? stackalloc byte[bitmapBytes] : new byte[bitmapBytes];
        bitmap.Clear();
        for (int l = 0; l < laneCount; l++)
        {
            if (HalfLaneChanged(xorData, pos + l * LaneSize))
                bitmap[l >> 3] |= (byte)(1 << (l & 7));
        }
        writer.Write(bitmap);

        // Pack the 2 XOR bytes of each changed lane, in lane order.
        int dataLen = LaneSize * changedLanes;
        Span<byte> packed = dataLen <= tempBuffer.Length ? tempBuffer[..dataLen] : new byte[dataLen];
        int cursor = 0;
        for (int l = 0; l < laneCount; l++)
        {
            int baseOff = pos + l * LaneSize;
            if (HalfLaneChanged(xorData, baseOff))
            {
                xorData.Slice(baseOff, LaneSize).CopyTo(packed.Slice(cursor, LaneSize));
                cursor += LaneSize;
            }
        }
        writer.Write(packed);

        counts.HalfPatternCount++;
        covered = span;
        return true;
    }

    // ChannelRun stride probe set: strides STRICTLY GREATER than the motif unit cap (8), where
    // the existing motif opcode can never lock. Probe order is the deterministic selection order
    // (12 and 16 — common interleaved record widths — first). Source of truth; mirrored by Zig
    // encoder.zig channel_run_strides.
    private static readonly int[] ChannelRunStrides = { 12, 16, 9, 10, 11, 13, 14, 15 };

    // ChannelRun 0x08 probe + emit. SINGLE SOURCE OF TRUTH for the channel-interleaved opcode;
    // mirrored byte-for-byte by Zig encoder.zig tryEmitChannelRun. Framing:
    //   [0x08][flags=0x00][stride:1][channelMask: ceil(stride/8), LSB-first][unitCount:7bit][packed: popcount(mask)*unitCount]
    // Targets channel-interleaved byte data with a stride GREATER than the motif unit cap (8) —
    // the precise gap motif leaves — where a fixed, small set of byte channels changes per unit,
    // each by a distinct value. Probed AFTER HalfRun (0x07) and FloatRun (0x06) in the basic-RLE
    // fallback branch. For each candidate stride S (ChannelRunStrides), derives the changed-channel
    // mask from the first unit at pos, extends over consecutive units whose changed bytes match the
    // SAME mask (every masked offset nonzero, every unmasked offset zero — varying-motif discipline
    // at stride > 8), and trims to the last matching unit. ALL-OPCODE-AWARE gate (TASK-0361 lesson):
    // emits ONLY when channelSize is strictly smaller than ALL of byte-RLE, the live motif/RLE cost
    // (EstimateMotifRleSizeForSpan), the FloatRun alternative (EstimateFloatRunSizeForSpan), AND the
    // HalfRun alternative (EstimateHalfRunSizeForSpan) over the same span. Strict improvement or
    // no-op — never a regression; yields to motif on stride <= 8 shapes (those never enter the probe
    // set) and to whichever of RLE/motif/Float/Half is cheaper on any span. (TASK-0363.)
    private static bool TryEmitChannelRun(ReadOnlySpan<byte> xorData, int pos, IBufferWriter<byte> writer,
        Span<byte> tempBuffer, ref DeltaZor.OpCodeCounts counts, out int covered)
    {
        covered = 0;
        int avail = xorData.Length - pos;

        foreach (int stride in ChannelRunStrides)
        {
            if (avail < 2 * stride) continue; // need at least 2 whole units

            // Derive the channel mask from the first unit at pos.
            uint mask = 0;
            int changedChannels = 0;
            for (int c = 0; c < stride; c++)
            {
                if (xorData[pos + c] != 0)
                {
                    mask |= (1u << c);
                    changedChannels++;
                }
            }
            if (changedChannels == 0) continue; // empty first unit — anchor must be non-empty

            // Extend over consecutive units whose changed bytes match the SAME mask exactly.
            int maxUnits = avail / stride;
            int unitCount = 1;
            for (int u = 1; u < maxUnits; u++)
            {
                int baseOff = pos + u * stride;
                bool matches = true;
                for (int c = 0; c < stride; c++)
                {
                    bool isSet = (mask & (1u << c)) != 0;
                    if (isSet)
                    {
                        if (xorData[baseOff + c] == 0) { matches = false; break; }
                    }
                    else
                    {
                        if (xorData[baseOff + c] != 0) { matches = false; break; }
                    }
                }
                if (!matches) break;
                unitCount++;
            }
            if (unitCount < 2) continue;

            int span = unitCount * stride;
            int channelMaskBytes = (stride + 7) / 8;
            int channelSize = 1 + 1 + 1 + channelMaskBytes + DeltaUtils.Get7BitEncodedSize(unitCount)
                              + changedChannels * unitCount;

            ReadOnlySpan<byte> spanSlice = xorData.Slice(pos, span);
            int rleSize = DeltaUtils.EstimateRLESizeForSpan(spanSlice);
            if (channelSize >= rleSize) continue; // strict improvement vs byte-RLE
            int motifRleSize = EstimateMotifRleSizeForSpan(spanSlice);
            if (channelSize >= motifRleSize) continue; // strict improvement vs motif/RLE
            int floatSize = EstimateFloatRunSizeForSpan(spanSlice, pos);
            if (channelSize >= floatSize) continue; // strict improvement vs FloatRun
            int halfSize = EstimateHalfRunSizeForSpan(spanSlice, pos);
            if (channelSize >= halfSize) continue; // strict improvement vs HalfRun

            // Emit opcode + flags + stride.
            Span<byte> oneByteSpan = stackalloc byte[1];
            oneByteSpan[0] = DeltaUtils.RLE_ChannelRun;
            writer.Write(oneByteSpan);
            oneByteSpan[0] = 0x00; // flags reserved
            writer.Write(oneByteSpan);
            oneByteSpan[0] = (byte)stride;
            writer.Write(oneByteSpan);

            // Channel mask (LSB-first per byte).
            Span<byte> channelMask = stackalloc byte[channelMaskBytes];
            channelMask.Clear();
            for (int c = 0; c < stride; c++)
            {
                if ((mask & (1u << c)) != 0)
                    channelMask[c >> 3] |= (byte)(1 << (c & 7));
            }
            writer.Write(channelMask);

            DeltaUtils.Write7BitEncodedInt(writer, unitCount);

            // Pack the changed bytes, unit-major then channel-order.
            int dataLen = changedChannels * unitCount;
            Span<byte> packed = dataLen <= tempBuffer.Length ? tempBuffer[..dataLen] : new byte[dataLen];
            int cursor = 0;
            for (int u = 0; u < unitCount; u++)
            {
                int baseOff = pos + u * stride;
                for (int c = 0; c < stride; c++)
                {
                    if ((mask & (1u << c)) != 0)
                        packed[cursor++] = xorData[baseOff + c];
                }
            }
            writer.Write(packed);

            counts.ChannelRunCount++;
            covered = span;
            return true;
        }

        return false;
    }

    public static DeltaZor.OpCodeCounts CreateRLEDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData,
        IBufferWriter<byte> writer, DeltaZor.DeltaOptions options)
    {
        var patternCounts = new DeltaZor.OpCodeCounts();
        int minLength = Math.Min(oldData.Length, newData.Length);
        Span<byte> oneByteSpan = stackalloc byte[1];
        Span<byte> tempBuffer = stackalloc byte[options.MaxStackBufferSize];

        // Arithmetic modes (0x09 Global / 0x0A Planar): probed FIRST over the WHOLE overlapping
        // region [0, minLength). Unlike the XOR-stream opcodes (0x00-0x08), arithmetic structure
        // (new = old + step) is destroyed by XOR (carries make old^(old+k) data-dependent noise),
        // so detection reads old/new DIRECTLY and the opcode applies ADDITIVELY at decode time
        // (the decoder pre-fills output with old, then adds the step). Each emits a single opcode
        // covering the whole region, gated to strictly beat the XOR/RLE alternative — else it
        // declines and the region falls through to the unchanged XOR/motif pipeline. (TASK-0364.)
        if (options.EnableArithmeticDetection)
        {
            if (TryEmitGlobalArithmetic(oldData, newData, minLength, writer, ref patternCounts))
            {
                AppendLengthOps(oldData, newData, writer, ref patternCounts);
                return patternCounts;
            }
            if (TryEmitPlanarArithmetic(oldData, newData, minLength, writer, ref patternCounts))
            {
                AppendLengthOps(oldData, newData, writer, ref patternCounts);
                return patternCounts;
            }
        }

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
        AppendLengthOps(oldData, newData, writer, ref patternCounts);

        return patternCounts;
    }

    // Emits the Extension (0x02) or Truncation (0x03) opcode for a length change between old and
    // new. Shared by the normal RLE/XOR path and the arithmetic (0x09/0x0A) whole-region path so
    // both reconstruct the trailing bytes identically. (TASK-0364 refactor — no behavior change.)
    private static void AppendLengthOps(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData,
        IBufferWriter<byte> writer, ref DeltaZor.OpCodeCounts patternCounts)
    {
        Span<byte> oneByteSpan = stackalloc byte[1];
        if (newData.Length > oldData.Length)
        {
            ReadOnlySpan<byte> extension = newData[oldData.Length..];
            oneByteSpan[0] = DeltaUtils.RLE_Extension;
            writer.Write(oneByteSpan);
            DeltaUtils.Write7BitEncodedInt(writer, extension.Length);
            writer.Write(extension);
            patternCounts.ExtensionCount++;
        }
        else if (newData.Length < oldData.Length)
        {
            oneByteSpan[0] = DeltaUtils.RLE_Truncation;
            writer.Write(oneByteSpan);
            DeltaUtils.Write7BitEncodedInt(writer, newData.Length);
            patternCounts.TruncationCount++;
        }
    }

    // Reads a little-endian unsigned integer of `width` bytes (1,2,4,8) starting at offset `off`.
    private static ulong ReadLE(ReadOnlySpan<byte> data, int off, int width)
    {
        ulong v = 0;
        for (int b = 0; b < width; b++)
            v |= (ulong)data[off + b] << (8 * b);
        return v;
    }

    // GlobalArithmetic 0x09 probe + emit. SINGLE SOURCE OF TRUTH; mirrored byte-for-byte by Zig
    // encoder.zig tryEmitGlobalArithmetic. Framing:
    //   [0x09][elemWidth:1][step: elemWidth bytes LE (two's-complement, wraparound)][laneCount:7bit]
    // Detects a UNIFORM additive step on fixed-width little-endian integer lanes across the WHOLE
    // overlapping region: for the first feasible width w in {4,2,1,8} (int32 first — the canonical
    // counter/gradient/+k case), require minLength % w == 0, >= 2 lanes, and that EVERY lane has
    // the identical wraparound difference step = (newLane - oldLane) mod 2^(8w). The step must be
    // non-zero (a zero step means new==old, which the ZeroRun path encodes in ~3 bytes — nothing
    // for arithmetic to win). Wraparound (two's-complement) is exact and lossless; NO clamp is used
    // (clamp would be lossy and cannot round-trip). Decode adds step into each lane of `output`
    // (pre-filled with old) — additive, not XOR. Emits ONLY when the framed size is strictly smaller
    // than the whole-region XOR byte-RLE alternative (EstimateXorRleSizeWholeRegion); else declines.
    private static bool TryEmitGlobalArithmetic(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData,
        int minLength, IBufferWriter<byte> writer, ref DeltaZor.OpCodeCounts counts)
    {
        if (minLength < 2) return false;

        foreach (int w in DeltaUtils.ArithmeticElemWidths)
        {
            if (minLength % w != 0) continue;
            int laneCount = minLength / w;
            if (laneCount < 2) continue;

            ulong widthMask = w == 8 ? ulong.MaxValue : (1UL << (8 * w)) - 1UL;
            ulong step = (ReadLE(newData, 0, w) - ReadLE(oldData, 0, w)) & widthMask;
            if (step == 0) continue; // no-op shift — ZeroRun encodes new==old far cheaper

            bool uniform = true;
            for (int l = 1; l < laneCount; l++)
            {
                int off = l * w;
                ulong d = (ReadLE(newData, off, w) - ReadLE(oldData, off, w)) & widthMask;
                if (d != step) { uniform = false; break; }
            }
            if (!uniform) continue;

            int arithSize = 1 + 1 + w + DeltaUtils.Get7BitEncodedSize(laneCount);
            int rleSize = DeltaUtils.EstimateXorRleSizeWholeRegion(oldData, newData, minLength);
            if (arithSize >= rleSize) continue; // strict improvement vs the XOR/RLE alternative

            Span<byte> head = stackalloc byte[2];
            head[0] = DeltaUtils.RLE_Arithmetic;
            head[1] = (byte)w;
            writer.Write(head);
            Span<byte> stepBytes = stackalloc byte[8];
            for (int b = 0; b < w; b++) stepBytes[b] = (byte)(step >> (8 * b));
            writer.Write(stepBytes[..w]);
            DeltaUtils.Write7BitEncodedInt(writer, laneCount);

            counts.ArithmeticCount++;
            return true;
        }

        return false;
    }

    // PlanarArithmetic 0x0A probe + emit. SINGLE SOURCE OF TRUTH; mirrored byte-for-byte by Zig
    // encoder.zig tryEmitPlanarArithmetic. Framing:
    //   [0x0A][planeCount:1][steps: planeCount bytes (byte wraparound)][unitCount:7bit]
    // Detects a PER-PLANE uniform additive byte step on interleaved byte planes across the WHOLE
    // region (e.g. an RGBA tint where each channel shifts by its own constant, including 0): for the
    // first feasible plane count P in {4,3,2}, require minLength % P == 0, >= 2 units, derive a step
    // per plane from the first unit, and require EVERY unit to match all P steps (byte wraparound).
    // At least one plane step must be non-zero (all-zero means new==old). Decode adds steps[p] into
    // each output byte at offset u*P+p (output pre-filled with old) — additive, not XOR, byte
    // wraparound (exact, no clamp). Probed AFTER GlobalArithmetic (which subsumes the all-planes-
    // equal-step case more compactly). Emits ONLY when strictly smaller than the whole-region XOR
    // byte-RLE alternative; else declines and the region falls through to the XOR/motif pipeline.
    private static bool TryEmitPlanarArithmetic(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData,
        int minLength, IBufferWriter<byte> writer, ref DeltaZor.OpCodeCounts counts)
    {
        if (minLength < 2) return false;

        foreach (int p in DeltaUtils.PlanarPlaneCounts)
        {
            if (minLength % p != 0) continue;
            int unitCount = minLength / p;
            if (unitCount < 2) continue;

            Span<byte> steps = stackalloc byte[8];
            for (int c = 0; c < p; c++)
                steps[c] = (byte)(newData[c] - oldData[c]);

            bool anyNonZero = false;
            for (int c = 0; c < p; c++) if (steps[c] != 0) { anyNonZero = true; break; }
            if (!anyNonZero) continue; // no-op shift

            bool uniform = true;
            for (int u = 1; u < unitCount && uniform; u++)
            {
                int baseOff = u * p;
                for (int c = 0; c < p; c++)
                {
                    if ((byte)(newData[baseOff + c] - oldData[baseOff + c]) != steps[c])
                    {
                        uniform = false;
                        break;
                    }
                }
            }
            if (!uniform) continue;

            int planarSize = 1 + 1 + p + DeltaUtils.Get7BitEncodedSize(unitCount);
            int rleSize = DeltaUtils.EstimateXorRleSizeWholeRegion(oldData, newData, minLength);
            if (planarSize >= rleSize) continue; // strict improvement vs the XOR/RLE alternative

            Span<byte> head = stackalloc byte[2];
            head[0] = DeltaUtils.RLE_Planar;
            head[1] = (byte)p;
            writer.Write(head);
            writer.Write(steps[..p]);
            DeltaUtils.Write7BitEncodedInt(writer, unitCount);

            counts.PlanarCount++;
            return true;
        }

        return false;
    }
}