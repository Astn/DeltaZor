const std = @import("std");
const utils = @import("utils.zig");

const bit_masks = utils.bit_masks;
const popCount32 = utils.popCount32;
const get7BitEncodedSize = utils.get7BitEncodedSize;
const write7BitEncodedIntDirect = utils.write7BitEncodedIntDirect;

// Stateful motif accumulator mirroring C# Encoder.cs MotifAccumulator. The encoder
// extends an active motif one unit at a time (TryExtend), emitting it only when it
// can no longer be extended (and ShouldEmit's savings test passes). This is the LIVE
// C# algorithm; see the constants block above for the differences vs FindMotifCandidate.
const MotifAccumulator = struct {
    unit_size: usize = 0,
    mask: u32 = 0,
    streak: usize = 0,
    is_uniform: bool = false,
    is_full: bool = false,
    changed_count: usize = 0,
    density: f32 = 0.0,
    start_pos: usize = 0,
    active: bool = false,

    fn reset(self: *MotifAccumulator) void {
        self.active = false;
        self.streak = 0;
        self.start_pos = 0;
    }

    fn coveredLength(self: *const MotifAccumulator) usize {
        return if (self.active) self.streak * self.unit_size else 0;
    }

    // Mirrors C# MotifAccumulator.TryStart (probes u=2..8 ascending).
    fn tryStart(self: *MotifAccumulator, xor_data: []const u8, pos: usize) bool {
        if (pos + motif_max_unit_size > xor_data.len) return false;

        var u: usize = 2;
        while (u <= motif_max_unit_size) : (u += 1) {
            var mask: u32 = 0;
            var pop: usize = 0;
            var i: usize = 0;
            while (i < u) : (i += 1) {
                if (xor_data[pos + i] != 0) {
                    mask |= bit_masks[i];
                    pop += 1;
                }
            }
            if (pop == 0) continue;

            const is_full = pop == u;
            const density = @as(f32, @floatFromInt(pop)) / @as(f32, @floatFromInt(u));
            if (!is_full and density >= motif_density_threshold) continue; // prune high-density masked

            if (pos + 2 * u > xor_data.len) continue; // need at least one repeat

            // Check pattern consistency for the first repeat.
            var matches = true;
            if (is_full) {
                matches = std.mem.eql(u8, xor_data[pos .. pos + u], xor_data[pos + u .. pos + 2 * u]);
            } else {
                i = 0;
                while (i < u) : (i += 1) {
                    const val = xor_data[pos + u + i];
                    if ((mask & bit_masks[i]) != 0) {
                        if (val == 0) {
                            matches = false;
                            break;
                        }
                    } else {
                        if (val != 0) {
                            matches = false;
                            break;
                        }
                    }
                }
            }
            if (!matches) continue;

            const uniform = is_full or checkUniform(xor_data, pos, u, mask, 2);

            self.unit_size = u;
            self.mask = mask;
            self.streak = 2;
            self.is_uniform = uniform;
            self.is_full = is_full;
            self.changed_count = pop;
            self.density = density;
            self.start_pos = pos;
            self.active = true;
            return true;
        }
        return false;
    }

    // Mirrors C# MotifAccumulator.TryExtend (no MaxMotifStreak cap — unbounded growth).
    fn tryExtend(self: *MotifAccumulator, xor_data: []const u8) bool {
        if (!self.active) return false;

        const next_start = self.start_pos + self.streak * self.unit_size;
        if (next_start + self.unit_size > xor_data.len) return false;

        if (self.is_full and self.is_uniform) {
            if (!std.mem.eql(u8, xor_data[next_start .. next_start + self.unit_size], xor_data[self.start_pos .. self.start_pos + self.unit_size]))
                return false;
        } else {
            var i: usize = 0;
            while (i < self.unit_size) : (i += 1) {
                const val = xor_data[next_start + i];
                if ((self.mask & bit_masks[i]) != 0) {
                    if (val == 0) return false;
                } else {
                    if (val != 0) return false;
                }
            }
            if (!self.is_uniform and !self.is_full) {
                if (!checkUniform(xor_data, self.start_pos, self.unit_size, self.mask, self.streak + 1))
                    return false;
            }
        }

        self.streak += 1;
        return true;
    }

    // Mirrors C# MotifAccumulator.ShouldEmit (default savingsThreshold = -0.1).
    fn shouldEmit(self: *const MotifAccumulator, xor_data: []const u8) bool {
        if (!self.active or self.streak < motif_min_streak) return false;

        const covered = self.coveredLength();
        var header_size: usize = 1 + 1 + get7BitEncodedSize(self.streak) + get7BitEncodedSize(self.unit_size);
        const data_size = self.changed_count * (if (self.is_uniform) @as(usize, 1) else self.streak);
        const motif_size = if (self.is_full)
            header_size + data_size
        else blk: {
            header_size += get7BitEncodedSize(@as(usize, @intCast(self.mask)));
            break :blk header_size + data_size;
        };

        const rle_size = estimateRLESizeForSpan(xor_data[self.start_pos .. self.start_pos + covered]);
        const savings = if (rle_size > 0)
            (@as(f32, @floatFromInt(rle_size)) - @as(f32, @floatFromInt(motif_size))) / @as(f32, @floatFromInt(rle_size))
        else
            0.0;
        return savings > motif_savings_threshold;
    }
};

// =====================================================================================
// Motif/RLE encoding constants.
//
// SINGLE SOURCE OF TRUTH: these MUST stay byte-for-byte in sync with the C# encoder
// (`src/csharp/DeltaZor/Utils.cs` DeltaUtils.* and `Encoder.cs` MotifAccumulator), or
// create-delta byte-parity (EPIC-0044) regresses. See `zig build test` create-delta
// byte-compare and `dotnet test DeltaZorTests` for the cross-language guard.
//
// The LIVE C# encoder is the `MotifAccumulator` (TryStart/TryExtend/ShouldEmit) in
// Encoder.cs — NOT the dead `FindMotifCandidate` method. The accumulator algorithm
// mirrored below differs from FindMotifCandidate in two ways that change emitted bytes:
//   1. TryStart probes unit sizes 2..8 ASCENDING (picks the smallest viable unit),
//      whereas FindMotifCandidate probed {4,8,2,3,5,6,7}.
//   2. TryExtend grows the streak unbounded (no MaxMotifStreak cap), so a long
//      repeat is emitted as ONE motif instead of being split at 50 units.
//   3. ShouldEmit uses savings threshold -0.1 (C# ShouldEmit default), not -0.5.
//
// FloatRun (0x06): see tryEmitFloatRun below — a float32-lane sparse run probed in the
// basic-RLE fallback branch (after motif TryStart fails), at 4-aligned positions only,
// gated to emit ONLY when strictly smaller than byte-RLE. Mirrors C# Encoder.cs
// TryEmitFloatRun (source of truth). TASK-0361 / EPIC-0045.
//
// HalfRun (0x07): see tryEmitHalfRun below — a float16 (2-byte) lane sparse run probed in
// the same fallback branch BEFORE FloatRun, at 2-aligned positions only, gated to emit ONLY
// when strictly smaller than byte-RLE, the motif/RLE cost, AND the FloatRun alternative
// (estimateFloatRunSizeForSpan). Mirrors C# Encoder.cs TryEmitHalfRun (source of truth).
// TASK-0362 / EPIC-0045.
// =====================================================================================
const motif_density_threshold: f32 = 0.7; // DeltaUtils.MotifDensityThreshold
const motif_savings_threshold: f32 = -0.1; // Encoder.cs ShouldEmit default savingsThreshold
const motif_min_streak: usize = 2; // DeltaUtils.MotifMinStreak
const motif_max_unit_size: usize = 8; // MotifAccumulator.TryStart probes u=2..8 ascending


fn writeXORDelta(old_data: []const u8, new_data: []const u8, output: []u8, start: usize, length: usize, options: utils.Options) void {
    _ = options;
    var i: usize = 0;
    while (i < length) : (i += 1) {
        output[i] = old_data[start + i] ^ new_data[start + i];
    }
}

// Emit an accumulated motif into the output buffer. Mirrors C# Encoder.cs EmitMotif.
fn emitMotif(acc: *const MotifAccumulator, xor_data: []const u8, buffer: []u8, data_pos: *usize, counts: *utils.OpCodeCounts) void {
    var temp_buffer: [4096]u8 = undefined;
    var pos_list: [32]usize = undefined;

    const is_uniform = acc.is_uniform;
    const is_full = acc.is_full;
    const msk = acc.mask;
    const unit = acc.unit_size;
    const reps = acc.streak;
    const changed = acc.changed_count;

    const opcode = if (is_uniform) utils.RLE_UNIFORM_MOTIF_REPEAT else utils.RLE_VARYING_MOTIF_REPEAT;
    buffer[data_pos.*] = opcode;
    data_pos.* += 1;
    const flags = if (is_full) @as(u8, 0x00) else @as(u8, 0x80);
    buffer[data_pos.*] = flags;
    data_pos.* += 1;
    write7BitEncodedIntDirect(buffer, data_pos, reps);
    write7BitEncodedIntDirect(buffer, data_pos, unit);
    if (!is_full) {
        write7BitEncodedIntDirect(buffer, data_pos, @as(usize, @intCast(msk)));
    }

    const data_len = changed * (if (is_uniform) @as(usize, 1) else reps);
    if (data_len > temp_buffer.len) @panic("temp buffer too small for motif data");
    if (is_full) {
        @memcpy(temp_buffer[0..data_len], xor_data[acc.start_pos .. acc.start_pos + data_len]);
    } else {
        var pp: usize = 0;
        var ii: usize = 0;
        while (ii < unit) : (ii += 1) {
            if ((msk & bit_masks[ii]) != 0) {
                pos_list[pp] = ii;
                pp += 1;
            }
        }
        var cursor: usize = 0;
        const max_rr = if (is_uniform) @as(usize, 1) else reps;
        var rr: usize = 0;
        while (rr < max_rr) : (rr += 1) {
            var jj: usize = 0;
            while (jj < changed) : (jj += 1) {
                const local_pos = pos_list[jj];
                temp_buffer[cursor] = xor_data[acc.start_pos + rr * unit + local_pos];
                cursor += 1;
            }
        }
    }
    @memcpy(buffer[data_pos.*..data_pos.* + data_len], temp_buffer[0..data_len]);
    data_pos.* += data_len;

    // Update counts (mirrors C#: AverageMaskDensity updated with acc.Density).
    const density = acc.density;
    const total_motif = counts.uniform_motif_count + counts.varying_motif_count + 1;
    const new_avg = (counts.average_mask_density * @as(f32, @floatFromInt(total_motif - 1)) + density) / @as(f32, @floatFromInt(total_motif));
    if (is_uniform) {
        counts.uniform_motif_count += 1;
    } else {
        counts.varying_motif_count += 1;
    }
    counts.average_mask_density = new_avg;
}

// Mirrors C# Encoder.cs EncodeXorWithMotifs: a stateful accumulator that extends a
// motif one unit at a time and emits it only when it can no longer be extended.
fn encodeXorWithMotifsDirect(xor_data: []const u8, buffer: []u8, data_pos: *usize, options: utils.Options, counts: *utils.OpCodeCounts) void {
    var temp_buffer: [4096]u8 = undefined;
    var pos: usize = 0;
    var acc = MotifAccumulator{};
    acc.reset();

    const motifs_enabled = options.enable_motif_detection;

    while (pos < xor_data.len) {
        // Try to extend the current motif.
        if (motifs_enabled and acc.active and acc.tryExtend(xor_data)) {
            pos += acc.unit_size;
            continue;
        }

        // Check if the current motif should be emitted.
        if (acc.active and acc.shouldEmit(xor_data)) {
            emitMotif(&acc, xor_data, buffer, data_pos, counts);
            pos = acc.start_pos + acc.coveredLength();
            acc.reset();
            continue;
        }

        // Reset accumulator if not extended or not emitting.
        if (acc.active) {
            acc.reset();
        }

        // Try to start a new motif.
        if (motifs_enabled and acc.tryStart(xor_data, pos)) {
            pos += acc.unit_size;
            continue;
        }

        // Try a ChannelRun (0x08) FIRST: channel-interleaved byte run at a stride > the motif
        // unit cap (8) — the gap motif cannot reach. Its all-opcode-aware gate (beats byte-RLE,
        // motif/RLE, FloatRun AND HalfRun over the span) lets it pre-empt Half/Float only when
        // genuinely cheaper; otherwise it declines and Half/Float probe next. Mirrors C#
        // Encoder.cs TryEmitChannelRun (source of truth) byte-for-byte. TASK-0363 / EPIC-0045.
        var channel_covered: usize = 0;
        if (tryEmitChannelRun(xor_data, pos, buffer, data_pos, counts, &channel_covered)) {
            pos += channel_covered;
            continue;
        }

        // Try a HalfRun (0x07): float16 (2-byte) lane sparse run, probed BEFORE FloatRun.
        // Its gate also beats the FloatRun alternative, so it fires only on genuinely 2-byte-
        // granular shapes and yields to FloatRun on 4-byte-dense shapes. Mirrors C# Encoder.cs
        // TryEmitHalfRun (source of truth) byte-for-byte. TASK-0362 / EPIC-0045.
        var half_covered: usize = 0;
        if (tryEmitHalfRun(xor_data, pos, buffer, data_pos, counts, &half_covered)) {
            pos += half_covered;
            continue;
        }

        // Try a FloatRun (0x06): float32-lane sparse run that motifs (unit cap 8) cannot
        // reach. Only at a 4-aligned position and only when strictly smaller than byte-RLE.
        // Mirrors C# Encoder.cs TryEmitFloatRun (source of truth) byte-for-byte.
        var float_covered: usize = 0;
        if (tryEmitFloatRun(xor_data, pos, buffer, data_pos, counts, &float_covered)) {
            pos += float_covered;
            continue;
        }

        // Fallback to a basic RLE run.
        const is_zero = xor_data[pos] == 0;
        var run_len: usize = 1;
        while (pos + run_len < xor_data.len and (xor_data[pos + run_len] == 0) == is_zero) run_len += 1;
        const opcode = if (is_zero) utils.RLE_ZERO_RUN else utils.RLE_NON_ZERO_RUN;
        buffer[data_pos.*] = opcode;
        data_pos.* += 1;
        write7BitEncodedIntDirect(buffer, data_pos, run_len);
        if (!is_zero) {
            if (run_len > temp_buffer.len) @panic("temp buffer too small for run data");
            @memcpy(temp_buffer[0..run_len], xor_data[pos .. pos + run_len]);
            @memcpy(buffer[data_pos.*..data_pos.* + run_len], temp_buffer[0..run_len]);
            data_pos.* += run_len;
        }
        if (is_zero) {
            counts.zero_run_count += 1;
        } else {
            counts.non_zero_run_count += 1;
        }
        pos += run_len;
    }

    // Emit any remaining motif (mirrors C# trailing ShouldEmit check).
    if (acc.active and acc.shouldEmit(xor_data)) {
        emitMotif(&acc, xor_data, buffer, data_pos, counts);
    }
}

// FloatRun 0x06 probe + emit. Mirrors C# Encoder.cs TryEmitFloatRun (SINGLE SOURCE OF
// TRUTH). Framing:
//   [0x06][flags=0x00][laneCount:7bit][laneBitmap: ceil(laneCount/8)][packedXor: 4*changedLanes]
// Treats the XOR stream as float32 lanes (4-byte units aligned to the stream origin).
// Requires the first lane changed, trims trailing zero lanes, and is MOTIF-AWARE: emits ONLY
// when strictly smaller than BOTH byte-RLE AND the actual motif/RLE encoder cost over the
// span (estimateMotifRleSizeForSpan). A mid-span motif-able block is priced cheaply by the
// motif estimate, so the gate rejects it (no regression vs the existing pipeline).
fn laneChanged(xor_data: []const u8, base_off: usize) bool {
    return xor_data[base_off] != 0 or xor_data[base_off + 1] != 0 or
        xor_data[base_off + 2] != 0 or xor_data[base_off + 3] != 0;
}

// Emitted byte size of a motif (mirrors emitMotif framing). Mirrors C# Encoder.cs
// MotifEmitSize (source of truth) for the FloatRun motif-alternative gate.
fn motifEmitSize(acc: *const MotifAccumulator) usize {
    var header_size: usize = 1 + 1 + get7BitEncodedSize(acc.streak) + get7BitEncodedSize(acc.unit_size);
    const data_len = acc.changed_count * (if (acc.is_uniform) @as(usize, 1) else acc.streak);
    if (acc.is_full) return header_size + data_len;
    header_size += get7BitEncodedSize(@as(usize, @intCast(acc.mask)));
    return header_size + data_len;
}

// Estimates the byte cost the existing motif + byte-RLE pipeline would produce for a span of
// the XOR stream, WITHOUT the FloatRun probe (no recursion). Pure size counter — never writes.
// Mirrors C# Encoder.cs EstimateMotifRleSizeForSpan (source of truth). FloatRun emits only
// when strictly smaller than this, so a mid-span motif-able block blocks FloatRun.
// (TASK-0361 codex REJECT B fix: gate vs the motif/RLE alternative, not just byte-RLE.)
fn estimateMotifRleSizeForSpan(span: []const u8) usize {
    var size: usize = 0;
    var pos: usize = 0;
    var acc = MotifAccumulator{};
    acc.reset();

    while (pos < span.len) {
        if (acc.active and acc.tryExtend(span)) {
            pos += acc.unit_size;
            continue;
        }
        if (acc.active and acc.shouldEmit(span)) {
            size += motifEmitSize(&acc);
            pos = acc.start_pos + acc.coveredLength();
            acc.reset();
            continue;
        }
        if (acc.active) acc.reset();
        if (acc.tryStart(span, pos)) {
            pos += acc.unit_size;
            continue;
        }
        // Basic RLE run fallback (matches encodeXorWithMotifsDirect).
        const is_zero = span[pos] == 0;
        var run_len: usize = 1;
        while (pos + run_len < span.len and (span[pos + run_len] == 0) == is_zero) run_len += 1;
        size += 1 + get7BitEncodedSize(run_len) + (if (is_zero) @as(usize, 0) else run_len);
        pos += run_len;
    }
    if (acc.active and acc.shouldEmit(span)) {
        size += motifEmitSize(&acc);
    }
    return size;
}

fn tryEmitFloatRun(xor_data: []const u8, pos: usize, buffer: []u8, data_pos: *usize, counts: *utils.OpCodeCounts, covered: *usize) bool {
    covered.* = 0;
    const lane_size: usize = 4;
    if ((pos & 3) != 0) return false; // require 4-aligned lane start
    const avail = xor_data.len - pos;
    const max_lanes = avail / lane_size;
    if (max_lanes < 2) return false; // need at least 2 lanes
    if (!laneChanged(xor_data, pos)) return false; // (a) no leading zero lanes

    // Count changed lanes and find the last changed lane (to trim trailing zeros).
    var changed_lanes: usize = 0;
    var last_changed: i64 = -1;
    var l: usize = 0;
    while (l < max_lanes) : (l += 1) {
        if (laneChanged(xor_data, pos + l * lane_size)) {
            changed_lanes += 1;
            last_changed = @intCast(l);
        }
    }

    const lane_count: usize = @intCast(last_changed + 1); // (b) trim trailing zero lanes
    if (lane_count < 2) return false;
    const span = lane_count * lane_size;

    const bitmap_bytes = (lane_count + 7) / 8;
    const float_size = 1 + 1 + get7BitEncodedSize(lane_count) + bitmap_bytes + lane_size * changed_lanes;
    const span_slice = xor_data[pos .. pos + span];
    const rle_size = estimateRLESizeForSpan(span_slice);
    if (float_size >= rle_size) return false; // strict improvement vs byte-RLE
    // Motif-aware gate: also beat the actual motif/RLE encoder cost over the span, so a
    // mid-span motif-able block cannot be swallowed at a net regression.
    const motif_rle_size = estimateMotifRleSizeForSpan(span_slice);
    if (float_size >= motif_rle_size) return false; // strict improvement vs motif/RLE too

    // Emit opcode + flags + laneCount.
    buffer[data_pos.*] = utils.RLE_FLOAT_RUN;
    data_pos.* += 1;
    buffer[data_pos.*] = 0x00; // flags reserved
    data_pos.* += 1;
    write7BitEncodedIntDirect(buffer, data_pos, lane_count);

    // Build + write the lane bitmap (LSB-first per byte).
    const bitmap_start = data_pos.*;
    @memset(buffer[bitmap_start .. bitmap_start + bitmap_bytes], 0);
    l = 0;
    while (l < lane_count) : (l += 1) {
        if (laneChanged(xor_data, pos + l * lane_size)) {
            buffer[bitmap_start + (l >> 3)] |= @as(u8, 1) << @intCast(l & 7);
        }
    }
    data_pos.* += bitmap_bytes;

    // Pack the 4 XOR bytes of each changed lane, in lane order.
    l = 0;
    while (l < lane_count) : (l += 1) {
        const base_off = pos + l * lane_size;
        if (laneChanged(xor_data, base_off)) {
            @memcpy(buffer[data_pos.* .. data_pos.* + lane_size], xor_data[base_off .. base_off + lane_size]);
            data_pos.* += lane_size;
        }
    }

    counts.float_pattern_count += 1;
    covered.* = span;
    return true;
}

fn halfLaneChanged(xor_data: []const u8, base_off: usize) bool {
    return xor_data[base_off] != 0 or xor_data[base_off + 1] != 0;
}

// Pure size counter: the byte cost a FloatRun (0x06) would emit over a span anchored at
// stream position `pos`. Returns the sentinel std.math.maxInt(usize) ("infeasible") when
// FloatRun cannot represent the span identically (pos not 4-aligned, span length not a
// multiple of 4, < 2 float lanes, or first float lane not changed). This is the FloatRun
// term of the HalfRun gate. Mirrors C# Encoder.cs EstimateFloatRunSizeForSpan (source of
// truth) byte-for-byte. Never writes bytes.
fn estimateFloatRunSizeForSpan(span: []const u8, pos: usize) usize {
    const lane_size: usize = 4;
    const infeasible = std.math.maxInt(usize);
    if ((pos & 3) != 0) return infeasible;
    if ((span.len & 3) != 0) return infeasible;
    const max_lanes = span.len / lane_size;
    if (max_lanes < 2) return infeasible;
    if (!laneChanged(span, 0)) return infeasible;

    var changed_lanes: usize = 0;
    var last_changed: i64 = -1;
    var l: usize = 0;
    while (l < max_lanes) : (l += 1) {
        if (laneChanged(span, l * lane_size)) {
            changed_lanes += 1;
            last_changed = @intCast(l);
        }
    }

    const lane_count: usize = @intCast(last_changed + 1);
    if (lane_count < 2) return infeasible;
    const bitmap_bytes = (lane_count + 7) / 8;
    return 1 + 1 + get7BitEncodedSize(lane_count) + bitmap_bytes + lane_size * changed_lanes;
}

// Pure size counter: the byte cost a HalfRun (0x07) would emit over a span anchored at stream
// position `pos` (the same span a candidate ChannelRun covers). Returns the sentinel
// std.math.maxInt(usize) ("infeasible") when HalfRun cannot represent the span identically (pos
// not 2-aligned, span length not a multiple of 2, < 2 half lanes, or first half-lane not changed).
// This is the HalfRun term of the ChannelRun gate. Mirrors C# Encoder.cs EstimateHalfRunSizeForSpan
// (source of truth) byte-for-byte. Never writes bytes. (TASK-0363.)
fn estimateHalfRunSizeForSpan(span: []const u8, pos: usize) usize {
    const lane_size: usize = 2;
    const infeasible = std.math.maxInt(usize);
    if ((pos & 1) != 0) return infeasible;
    if ((span.len & 1) != 0) return infeasible;
    const max_lanes = span.len / lane_size;
    if (max_lanes < 2) return infeasible;
    if (!halfLaneChanged(span, 0)) return infeasible;

    var changed_lanes: usize = 0;
    var last_changed: i64 = -1;
    var l: usize = 0;
    while (l < max_lanes) : (l += 1) {
        if (halfLaneChanged(span, l * lane_size)) {
            changed_lanes += 1;
            last_changed = @intCast(l);
        }
    }

    const lane_count: usize = @intCast(last_changed + 1);
    if (lane_count < 2) return infeasible;
    const bitmap_bytes = (lane_count + 7) / 8;
    return 1 + 1 + get7BitEncodedSize(lane_count) + bitmap_bytes + lane_size * changed_lanes;
}

// HalfRun 0x07 probe + emit. Mirrors C# Encoder.cs TryEmitHalfRun (SINGLE SOURCE OF TRUTH).
// Framing:
//   [0x07][flags=0x00][laneCount:7bit][laneBitmap: ceil(laneCount/8)][packedXor: 2*changedLanes]
// Treats the XOR stream as float16 lanes (2-byte units). Probed BEFORE FloatRun, at 2-aligned
// positions. Requires first half-lane changed, trims trailing zero lanes, and is MOTIF-AWARE
// + FloatRun-aware: emits ONLY when halfSize is strictly smaller than ALL of byte-RLE, the
// live motif/RLE cost (estimateMotifRleSizeForSpan), AND the FloatRun alternative
// (estimateFloatRunSizeForSpan) over the same span. Strict improvement or no-op.
fn tryEmitHalfRun(xor_data: []const u8, pos: usize, buffer: []u8, data_pos: *usize, counts: *utils.OpCodeCounts, covered: *usize) bool {
    covered.* = 0;
    const lane_size: usize = 2;
    if ((pos & 1) != 0) return false; // require 2-aligned lane start
    const avail = xor_data.len - pos;
    const max_lanes = avail / lane_size;
    if (max_lanes < 2) return false; // need at least 2 lanes
    if (!halfLaneChanged(xor_data, pos)) return false; // (a) no leading zero lanes

    // Count changed lanes and find the last changed lane (to trim trailing zeros).
    var changed_lanes: usize = 0;
    var last_changed: i64 = -1;
    var l: usize = 0;
    while (l < max_lanes) : (l += 1) {
        if (halfLaneChanged(xor_data, pos + l * lane_size)) {
            changed_lanes += 1;
            last_changed = @intCast(l);
        }
    }

    const lane_count: usize = @intCast(last_changed + 1); // (b) trim trailing zero lanes
    if (lane_count < 2) return false;
    const span = lane_count * lane_size;

    const bitmap_bytes = (lane_count + 7) / 8;
    const half_size = 1 + 1 + get7BitEncodedSize(lane_count) + bitmap_bytes + lane_size * changed_lanes;
    const span_slice = xor_data[pos .. pos + span];
    const rle_size = estimateRLESizeForSpan(span_slice);
    if (half_size >= rle_size) return false; // strict improvement vs byte-RLE
    const motif_rle_size = estimateMotifRleSizeForSpan(span_slice);
    if (half_size >= motif_rle_size) return false; // strict improvement vs motif/RLE
    // FloatRun-aware gate: also beat what a FloatRun would emit over the same span.
    const float_size = estimateFloatRunSizeForSpan(span_slice, pos);
    if (half_size >= float_size) return false; // strict improvement vs FloatRun too

    // Emit opcode + flags + laneCount.
    buffer[data_pos.*] = utils.RLE_HALF_RUN;
    data_pos.* += 1;
    buffer[data_pos.*] = 0x00; // flags reserved
    data_pos.* += 1;
    write7BitEncodedIntDirect(buffer, data_pos, lane_count);

    // Build + write the lane bitmap (LSB-first per byte).
    const bitmap_start = data_pos.*;
    @memset(buffer[bitmap_start .. bitmap_start + bitmap_bytes], 0);
    l = 0;
    while (l < lane_count) : (l += 1) {
        if (halfLaneChanged(xor_data, pos + l * lane_size)) {
            buffer[bitmap_start + (l >> 3)] |= @as(u8, 1) << @intCast(l & 7);
        }
    }
    data_pos.* += bitmap_bytes;

    // Pack the 2 XOR bytes of each changed lane, in lane order.
    l = 0;
    while (l < lane_count) : (l += 1) {
        const base_off = pos + l * lane_size;
        if (halfLaneChanged(xor_data, base_off)) {
            @memcpy(buffer[data_pos.* .. data_pos.* + lane_size], xor_data[base_off .. base_off + lane_size]);
            data_pos.* += lane_size;
        }
    }

    counts.half_pattern_count += 1;
    covered.* = span;
    return true;
}

// ChannelRun stride probe set: strides STRICTLY GREATER than the motif unit cap (8), where the
// existing motif opcode can never lock. Probe order is the deterministic selection order. Mirrors
// C# Encoder.cs ChannelRunStrides (source of truth) exactly.
const channel_run_strides = [_]usize{ 12, 16, 9, 10, 11, 13, 14, 15 };

// ChannelRun 0x08 probe + emit. Mirrors C# Encoder.cs TryEmitChannelRun (SINGLE SOURCE OF TRUTH).
// Framing:
//   [0x08][flags=0x00][stride:1][channelMask: ceil(stride/8), LSB-first][unitCount:7bit][packed: popcount(mask)*unitCount]
// Targets channel-interleaved byte data with a stride GREATER than the motif unit cap (8) — the
// gap motif leaves — where a fixed small set of byte channels changes per unit, each by a distinct
// value. Probed BEFORE HalfRun/FloatRun because it carries the most complete gate: emits ONLY when
// channelSize is strictly smaller than ALL of byte-RLE, the live motif/RLE cost, the FloatRun
// alternative AND the HalfRun alternative over the same span. Pre-empts Half/Float only when
// genuinely cheaper; otherwise declines (no double-fire, no regression).
fn tryEmitChannelRun(xor_data: []const u8, pos: usize, buffer: []u8, data_pos: *usize, counts: *utils.OpCodeCounts, covered: *usize) bool {
    covered.* = 0;
    const avail = xor_data.len - pos;

    for (channel_run_strides) |stride| {
        if (avail < 2 * stride) continue; // need at least 2 whole units

        // Derive the channel mask from the first unit at pos.
        var mask: u32 = 0;
        var changed_channels: usize = 0;
        var c: usize = 0;
        while (c < stride) : (c += 1) {
            if (xor_data[pos + c] != 0) {
                mask |= bit_masks[c];
                changed_channels += 1;
            }
        }
        if (changed_channels == 0) continue; // empty first unit — anchor must be non-empty

        // Extend over consecutive units whose changed bytes match the SAME mask exactly.
        const max_units = avail / stride;
        var unit_count: usize = 1;
        var u: usize = 1;
        while (u < max_units) : (u += 1) {
            const base_off = pos + u * stride;
            var matches = true;
            c = 0;
            while (c < stride) : (c += 1) {
                const is_set = (mask & bit_masks[c]) != 0;
                if (is_set) {
                    if (xor_data[base_off + c] == 0) {
                        matches = false;
                        break;
                    }
                } else {
                    if (xor_data[base_off + c] != 0) {
                        matches = false;
                        break;
                    }
                }
            }
            if (!matches) break;
            unit_count += 1;
        }
        if (unit_count < 2) continue;

        const span = unit_count * stride;
        const channel_mask_bytes = (stride + 7) / 8;
        const channel_size = 1 + 1 + 1 + channel_mask_bytes + get7BitEncodedSize(unit_count) + changed_channels * unit_count;

        const span_slice = xor_data[pos .. pos + span];
        const rle_size = estimateRLESizeForSpan(span_slice);
        if (channel_size >= rle_size) continue; // strict improvement vs byte-RLE
        const motif_rle_size = estimateMotifRleSizeForSpan(span_slice);
        if (channel_size >= motif_rle_size) continue; // strict improvement vs motif/RLE
        const float_size = estimateFloatRunSizeForSpan(span_slice, pos);
        if (channel_size >= float_size) continue; // strict improvement vs FloatRun
        const half_size = estimateHalfRunSizeForSpan(span_slice, pos);
        if (channel_size >= half_size) continue; // strict improvement vs HalfRun

        // Emit opcode + flags + stride.
        buffer[data_pos.*] = utils.RLE_CHANNEL_RUN;
        data_pos.* += 1;
        buffer[data_pos.*] = 0x00; // flags reserved
        data_pos.* += 1;
        buffer[data_pos.*] = @intCast(stride);
        data_pos.* += 1;

        // Channel mask (LSB-first per byte).
        const mask_start = data_pos.*;
        @memset(buffer[mask_start .. mask_start + channel_mask_bytes], 0);
        c = 0;
        while (c < stride) : (c += 1) {
            if ((mask & bit_masks[c]) != 0) {
                buffer[mask_start + (c >> 3)] |= @as(u8, 1) << @intCast(c & 7);
            }
        }
        data_pos.* += channel_mask_bytes;

        write7BitEncodedIntDirect(buffer, data_pos, unit_count);

        // Pack the changed bytes, unit-major then channel-order.
        u = 0;
        while (u < unit_count) : (u += 1) {
            const base_off = pos + u * stride;
            c = 0;
            while (c < stride) : (c += 1) {
                if ((mask & bit_masks[c]) != 0) {
                    buffer[data_pos.*] = xor_data[base_off + c];
                    data_pos.* += 1;
                }
            }
        }

        counts.channel_run_count += 1;
        covered.* = span;
        return true;
    }

    return false;
}

fn createRLEDeltaDirect(old_data: []const u8, new_data: []const u8, buffer: []u8, data_pos: *usize, options: utils.Options, counts: *utils.OpCodeCounts) void {
    const min_len = @min(old_data.len, new_data.len);
    var temp_buffer: [4096]u8 = undefined;

    // Arithmetic modes (0x09 Global / 0x0A Planar): probed FIRST over the WHOLE region. Detection
    // reads old/new DIRECTLY (XOR destroys arithmetic structure via carries) and the opcode applies
    // ADDITIVELY at decode (output is pre-filled with old, then the step is added). Each emits a
    // single whole-region opcode, gated to strictly beat the XOR/RLE alternative; else it declines
    // and the region falls through to the unchanged XOR/motif pipeline. Mirrors C# Encoder.cs
    // TryEmitGlobalArithmetic / TryEmitPlanarArithmetic (source of truth). (TASK-0364.)
    if (options.enable_arithmetic_detection) {
        if (tryEmitGlobalArithmetic(old_data, new_data, min_len, buffer, data_pos, counts)) {
            appendLengthOps(old_data, new_data, buffer, data_pos, counts);
            return;
        }
        if (tryEmitPlanarArithmetic(old_data, new_data, min_len, buffer, data_pos, counts)) {
            appendLengthOps(old_data, new_data, buffer, data_pos, counts);
            return;
        }
        if (tryEmitRunArithmetic(old_data, new_data, min_len, buffer, data_pos, counts)) {
            appendLengthOps(old_data, new_data, buffer, data_pos, counts);
            return;
        }
    }

    const use_full_xor = min_len <= options.max_stack_buffer_size and options.enable_motif_detection;
    if (use_full_xor) {
        const xor_buffer = temp_buffer[0..min_len];
        writeXORDelta(old_data, new_data, xor_buffer, 0, min_len, options);
        encodeXorWithMotifsDirect(xor_buffer, buffer, data_pos, options, counts);
    } else {
        // Streaming basic RLE
        var pos: usize = 0;
        while (pos < min_len) {
            const run_start = pos;
            const is_zero = old_data[pos] == new_data[pos];
            while (pos < min_len and (old_data[pos] == new_data[pos]) == is_zero) pos += 1;
            const run_len = pos - run_start;
        const opcode = if (is_zero) utils.RLE_ZERO_RUN else utils.RLE_NON_ZERO_RUN;
            buffer[data_pos.*] = opcode;
            data_pos.* += 1;
            write7BitEncodedIntDirect(buffer, data_pos, run_len);
            if (!is_zero) {
                if (run_len > temp_buffer.len) @panic("temp buffer too small for run data");
                writeXORDelta(old_data, new_data, temp_buffer[0..run_len], run_start, run_len, options);
                @memcpy(buffer[data_pos.*..data_pos.* + run_len], temp_buffer[0..run_len]);
                data_pos.* += run_len;
            }
            if (is_zero) counts.zero_run_count += 1 else counts.non_zero_run_count += 1;
        }
    }

    // Extension or truncation
    appendLengthOps(old_data, new_data, buffer, data_pos, counts);
}

// Emits the Extension (0x02) or Truncation (0x03) opcode for a length change. Shared by the normal
// RLE/XOR path and the arithmetic (0x09/0x0A) whole-region path. Mirrors C# Encoder.cs
// AppendLengthOps (source of truth). (TASK-0364 refactor — no behavior change.)
fn appendLengthOps(old_data: []const u8, new_data: []const u8, buffer: []u8, data_pos: *usize, counts: *utils.OpCodeCounts) void {
    if (new_data.len > old_data.len) {
        const extension = new_data[old_data.len..];
        const extension_len = extension.len;
        buffer[data_pos.*] = utils.RLE_EXTENSION;
        data_pos.* += 1;
        write7BitEncodedIntDirect(buffer, data_pos, extension_len);
        @memcpy(buffer[data_pos.*..data_pos.* + extension_len], extension);
        data_pos.* += extension_len;
        counts.extension_count += 1;
    } else if (new_data.len < old_data.len) {
        buffer[data_pos.*] = utils.RLE_TRUNCATION;
        data_pos.* += 1;
        write7BitEncodedIntDirect(buffer, data_pos, new_data.len);
        counts.truncation_count += 1;
    }
}

// Reads a little-endian unsigned integer of `width` bytes (1,2,4,8) at offset `off`. Mirrors C#
// Encoder.cs ReadLE.
fn readLE(data: []const u8, off: usize, width: usize) u64 {
    var v: u64 = 0;
    var b: usize = 0;
    while (b < width) : (b += 1) {
        v |= @as(u64, data[off + b]) << @intCast(8 * b);
    }
    return v;
}

// Allocation-free byte-RLE size estimate of the XOR stream (old^new) over [0, length), reading
// old/new directly (works for arbitrarily large buffers). Mirrors C# DeltaUtils.EstimateXorRle
// SizeWholeRegion (source of truth). The cost the arithmetic opcodes must strictly beat.
fn estimateXorRleSizeWholeRegion(old_data: []const u8, new_data: []const u8, length: usize) usize {
    var size: usize = 0;
    var i: usize = 0;
    while (i < length) {
        const is_zero = old_data[i] == new_data[i];
        var run_len: usize = 1;
        while (i + run_len < length and (old_data[i + run_len] == new_data[i + run_len]) == is_zero) run_len += 1;
        size += 1 + get7BitEncodedSize(run_len);
        if (!is_zero) size += run_len;
        i += run_len;
    }
    return size;
}

// GlobalArithmetic 0x09 probe + emit. Mirrors C# Encoder.cs TryEmitGlobalArithmetic (SINGLE SOURCE
// OF TRUTH). Framing:
//   [0x09][elemWidth:1][step: elemWidth bytes LE (two's-complement, wraparound)][laneCount:7bit]
// Detects a uniform additive step on fixed-width LE integer lanes across the whole region (int32
// first). Step must be non-zero; wraparound is exact (no clamp). Emits ONLY when strictly smaller
// than the whole-region XOR byte-RLE alternative.
fn tryEmitGlobalArithmetic(old_data: []const u8, new_data: []const u8, min_len: usize, buffer: []u8, data_pos: *usize, counts: *utils.OpCodeCounts) bool {
    if (min_len < 2) return false;

    for (utils.arithmetic_elem_widths) |w| {
        if (min_len % w != 0) continue;
        const lane_count = min_len / w;
        if (lane_count < 2) continue;

        const width_mask: u64 = if (w == 8) std.math.maxInt(u64) else (@as(u64, 1) << @intCast(8 * w)) - 1;
        const step = (readLE(new_data, 0, w) -% readLE(old_data, 0, w)) & width_mask;
        if (step == 0) continue; // no-op shift

        var uniform = true;
        var l: usize = 1;
        while (l < lane_count) : (l += 1) {
            const off = l * w;
            const d = (readLE(new_data, off, w) -% readLE(old_data, off, w)) & width_mask;
            if (d != step) {
                uniform = false;
                break;
            }
        }
        if (!uniform) continue;

        const arith_size = 1 + 1 + w + get7BitEncodedSize(lane_count);
        const rle_size = estimateXorRleSizeWholeRegion(old_data, new_data, min_len);
        if (arith_size >= rle_size) continue; // strict improvement vs XOR/RLE

        buffer[data_pos.*] = utils.RLE_ARITHMETIC;
        data_pos.* += 1;
        buffer[data_pos.*] = @intCast(w);
        data_pos.* += 1;
        var b: usize = 0;
        while (b < w) : (b += 1) {
            buffer[data_pos.*] = @truncate(step >> @intCast(8 * b));
            data_pos.* += 1;
        }
        write7BitEncodedIntDirect(buffer, data_pos, lane_count);

        counts.arithmetic_count += 1;
        return true;
    }

    return false;
}

// PlanarArithmetic 0x0A probe + emit. Mirrors C# Encoder.cs TryEmitPlanarArithmetic (SINGLE SOURCE
// OF TRUTH). Framing:
//   [0x0A][planeCount:1][steps: planeCount bytes (byte wraparound)][unitCount:7bit]
// Detects a per-plane uniform additive byte step on interleaved byte planes across the whole region
// (e.g. an RGBA tint, each channel its own constant incl. 0). At least one step non-zero. Decode
// adds steps[p] into output[u*P+p] (byte wraparound, exact). Emits ONLY when strictly smaller than
// the whole-region XOR byte-RLE alternative.
fn tryEmitPlanarArithmetic(old_data: []const u8, new_data: []const u8, min_len: usize, buffer: []u8, data_pos: *usize, counts: *utils.OpCodeCounts) bool {
    if (min_len < 2) return false;

    for (utils.planar_plane_counts) |p| {
        if (min_len % p != 0) continue;
        const unit_count = min_len / p;
        if (unit_count < 2) continue;

        var steps: [8]u8 = undefined;
        var c: usize = 0;
        while (c < p) : (c += 1) {
            steps[c] = new_data[c] -% old_data[c];
        }

        var any_non_zero = false;
        c = 0;
        while (c < p) : (c += 1) {
            if (steps[c] != 0) {
                any_non_zero = true;
                break;
            }
        }
        if (!any_non_zero) continue; // no-op shift

        var uniform = true;
        var u: usize = 1;
        while (u < unit_count and uniform) : (u += 1) {
            const base_off = u * p;
            c = 0;
            while (c < p) : (c += 1) {
                if ((new_data[base_off + c] -% old_data[base_off + c]) != steps[c]) {
                    uniform = false;
                    break;
                }
            }
        }
        if (!uniform) continue;

        const planar_size = 1 + 1 + p + get7BitEncodedSize(unit_count);
        const rle_size = estimateXorRleSizeWholeRegion(old_data, new_data, min_len);
        if (planar_size >= rle_size) continue; // strict improvement vs XOR/RLE

        buffer[data_pos.*] = utils.RLE_PLANAR;
        data_pos.* += 1;
        buffer[data_pos.*] = @intCast(p);
        data_pos.* += 1;
        c = 0;
        while (c < p) : (c += 1) {
            buffer[data_pos.*] = steps[c];
            data_pos.* += 1;
        }
        write7BitEncodedIntDirect(buffer, data_pos, unit_count);

        counts.planar_count += 1;
        return true;
    }

    return false;
}

// Saturating add of a signed step to a byte, clamped to [0,255]. Mirrors C# Encoder.cs ClampAdd.
fn clampAdd(v: u8, step: i8) u8 {
    const r: i32 = @as(i32, v) + @as(i32, step);
    if (r < 0) return 0;
    if (r > 255) return 255;
    return @intCast(r);
}

// Derives the signed clamp step for a run starting at `start`. Mirrors C# Encoder.cs
// TryDeriveClampStep. A clamp run's intended step is unambiguous only when the run contains a byte
// that does NOT saturate (there, step = new-old exactly). Returns false (step left 0) when the run
// is empty, has no non-boundary byte, or the derived step does not explain the first byte.
fn tryDeriveClampStep(old_data: []const u8, new_data: []const u8, min_len: usize, start: usize, step: *i8) bool {
    step.* = 0;
    var j: usize = start;
    while (j < min_len) : (j += 1) {
        if (old_data[j] == new_data[j]) {
            if (j == start) return false else break;
        }
        if (new_data[j] != 0 and new_data[j] != 255) {
            const d: i32 = @as(i32, new_data[j]) - @as(i32, old_data[j]);
            if (d < -128 or d > 127) return false;
            step.* = @intCast(d);
            return new_data[start] == clampAdd(old_data[start], step.*);
        }
    }
    return false; // all-boundary run is ambiguous; wraparound handles it
}

// Scans the longest arithmetic run starting at `start`. Mirrors C# Encoder.cs TryScanArithmeticRun.
// Tries wraparound first (longest run with a single byte step s where new==old+%s), then clamp
// (longest run where new==clamp(old+(i8)s)); picks the longer (wraparound wins ties). Requires a
// non-zero step and >= RUN_ARITHMETIC_MIN_RUN bytes. Returns false if neither qualifies.
fn tryScanArithmeticRun(old_data: []const u8, new_data: []const u8, min_len: usize, start: usize, step: *u8, clamp: *bool, run_len: *usize) bool {
    step.* = 0;
    clamp.* = false;
    run_len.* = 0;

    if (old_data[start] == new_data[start]) return false; // step 0 — ZeroRun is cheaper

    // Wraparound.
    const w_step: u8 = new_data[start] -% old_data[start];
    var w_len: usize = 1;
    while (start + w_len < min_len and (new_data[start + w_len] -% old_data[start + w_len]) == w_step) w_len += 1;

    // Clamp.
    var c_len: usize = 0;
    var c_step: i8 = 0;
    var derived: i8 = 0;
    if (tryDeriveClampStep(old_data, new_data, min_len, start, &derived)) {
        var len: usize = 1;
        while (start + len < min_len and new_data[start + len] == clampAdd(old_data[start + len], derived)) len += 1;
        if (len > c_len) {
            c_len = len;
            c_step = derived;
        }
    }

    if (w_len >= c_len and w_len >= utils.RUN_ARITHMETIC_MIN_RUN) {
        step.* = w_step;
        clamp.* = false;
        run_len.* = w_len;
        return true;
    }
    if (c_len >= utils.RUN_ARITHMETIC_MIN_RUN) {
        step.* = @bitCast(c_step);
        clamp.* = true;
        run_len.* = c_len;
        return true;
    }
    return false;
}

// Scans a filler span starting at `start`: the maximal span that does NOT begin an arithmetic run.
// Mirrors C# Encoder.cs ScanFiller.
fn scanFiller(old_data: []const u8, new_data: []const u8, min_len: usize, start: usize) usize {
    var j: usize = start;
    var s: u8 = undefined;
    var c: bool = undefined;
    var rl: usize = undefined;
    while (true) {
        j += 1;
        if (j >= min_len) break;
        if (tryScanArithmeticRun(old_data, new_data, min_len, j, &s, &c, &rl)) break;
    }
    return j - start;
}

// Size of the ZeroRun/NonZeroRun encoding for a filler span. Mirrors C# Encoder.cs SizeFiller.
fn sizeFiller(old_data: []const u8, new_data: []const u8, start: usize, len: usize) usize {
    var size: usize = 0;
    var i: usize = start;
    const end = start + len;
    while (i < end) {
        const is_zero = old_data[i] == new_data[i];
        var run_len: usize = 1;
        while (i + run_len < end and (old_data[i + run_len] == new_data[i + run_len]) == is_zero) run_len += 1;
        size += 1 + get7BitEncodedSize(run_len);
        if (!is_zero) size += run_len;
        i += run_len;
    }
    return size;
}

// Emits the ZeroRun/NonZeroRun encoding for a filler span, matching the baseline streaming RLE path
// byte-for-byte. Mirrors C# Encoder.cs EmitFiller.
fn emitFiller(old_data: []const u8, new_data: []const u8, start: usize, len: usize, buffer: []u8, data_pos: *usize, counts: *utils.OpCodeCounts) void {
    var i: usize = start;
    const end = start + len;
    while (i < end) {
        const is_zero = old_data[i] == new_data[i];
        var run_len: usize = 1;
        while (i + run_len < end and (old_data[i + run_len] == new_data[i + run_len]) == is_zero) run_len += 1;
        buffer[data_pos.*] = if (is_zero) utils.RLE_ZERO_RUN else utils.RLE_NON_ZERO_RUN;
        data_pos.* += 1;
        write7BitEncodedIntDirect(buffer, data_pos, run_len);
        if (!is_zero) {
            var k: usize = 0;
            while (k < run_len) : (k += 1) {
                buffer[data_pos.*] = old_data[i + k] ^ new_data[i + k];
                data_pos.* += 1;
            }
            counts.non_zero_run_count += 1;
        } else {
            counts.zero_run_count += 1;
        }
        i += run_len;
    }
}

// RunArithmetic 0x0B probe + emit. Mirrors C# Encoder.cs TryEmitRunArithmetic (SINGLE SOURCE OF
// TRUTH). Framing: [0x0B][flags:1][step:1][runLen:7bit]. Per-run/segmented byte arithmetic, probed
// AFTER 0x09/0x0A decline and BEFORE the XOR/motif pipeline. Greedily segments [0,min_len) into
// arithmetic runs (0x0B) and ZeroRun/NonZeroRun fillers; emits the whole plan ONLY when strictly
// smaller than the whole-region XOR byte-RLE alternative AND at least one 0x0B run is present.
// Clamp is LOSSLESS (encoder verifies new==clamp(old+step); decode replays it on the untouched old).
fn tryEmitRunArithmetic(old_data: []const u8, new_data: []const u8, min_len: usize, buffer: []u8, data_pos: *usize, counts: *utils.OpCodeCounts) bool {
    if (min_len < utils.RUN_ARITHMETIC_MIN_RUN) return false;

    var step: u8 = undefined;
    var clamp: bool = undefined;
    var run_len: usize = undefined;

    // First pass: size the candidate plan and count 0x0B runs without emitting.
    var plan_size: usize = 0;
    var run_count: usize = 0;
    var i: usize = 0;
    while (i < min_len) {
        if (tryScanArithmeticRun(old_data, new_data, min_len, i, &step, &clamp, &run_len)) {
            plan_size += 1 + 1 + 1 + get7BitEncodedSize(run_len);
            run_count += 1;
            i += run_len;
        } else {
            const filler_len = scanFiller(old_data, new_data, min_len, i);
            plan_size += sizeFiller(old_data, new_data, i, filler_len);
            i += filler_len;
        }
    }

    if (run_count == 0) return false;

    const rle_size = estimateXorRleSizeWholeRegion(old_data, new_data, min_len);
    if (plan_size >= rle_size) return false; // strict improvement vs XOR/RLE

    // Second pass: emit.
    i = 0;
    while (i < min_len) {
        if (tryScanArithmeticRun(old_data, new_data, min_len, i, &step, &clamp, &run_len)) {
            buffer[data_pos.*] = utils.RLE_RUN_ARITHMETIC;
            data_pos.* += 1;
            buffer[data_pos.*] = if (clamp) 0x01 else 0x00;
            data_pos.* += 1;
            buffer[data_pos.*] = step;
            data_pos.* += 1;
            write7BitEncodedIntDirect(buffer, data_pos, run_len);
            counts.run_arithmetic_count += 1;
            i += run_len;
        } else {
            const filler_len = scanFiller(old_data, new_data, min_len, i);
            emitFiller(old_data, new_data, i, filler_len, buffer, data_pos, counts);
            i += filler_len;
        }
    }

    return true;
}

pub fn createDeltaWithStats(old_data: []const u8, new_data: []const u8, allocator: std.mem.Allocator, options: utils.Options, stats: *utils.Stats) ![]u8 {
    const used_rle = true;

    // Conservative initial allocation for RLE attempt (large enough for worst-case RLE expansion)
    const len_diff = if (old_data.len > new_data.len) old_data.len - new_data.len else 0;
    const estimated_capacity = new_data.len * 2 + len_diff + 4096; // safe upper bound for RLE
    const initial_total_size = 9 + estimated_capacity;
    var buffer = try allocator.alloc(u8, initial_total_size);
    var pos: usize = 0;

    // Write header (common)
    const len32 = @as(u32, @intCast(new_data.len));
    buffer[pos] = @truncate(len32); pos += 1;
    buffer[pos] = @truncate(len32 >> 8); pos += 1;
    buffer[pos] = @truncate(len32 >> 16); pos += 1;
    buffer[pos] = @truncate(len32 >> 24); pos += 1;
    const header_end = pos;
    // Write compression_type placeholder; will be patched with checksum flag after encoding
    buffer[pos] = if (used_rle) @as(u8, 0x00) else @as(u8, 0x01); pos += 1;
    const data_start = pos;

    var pattern_counts = utils.OpCodeCounts{};
    if (used_rle) {
        createRLEDeltaDirect(old_data, new_data, buffer, &pos, options, &pattern_counts);
    } else {
        @memcpy(buffer[pos..pos + new_data.len], new_data);
        pos += new_data.len;
    }

    const rle_data_len = pos - data_start;
    var final_used_rle = used_rle;
    // Fallback threshold honors options.compression_threshold (parity with C#:
    // `dataSpan.Length > newData.Length * options.CompressionThreshold`, double arithmetic).
    if (used_rle and @as(f64, @floatFromInt(rle_data_len)) > @as(f64, @floatFromInt(new_data.len)) * options.compression_threshold) {
        // Fallback to full replace
        final_used_rle = false;
        // Realloc to exact size for full (+ optional checksum)
        const full_total_size = 5 + new_data.len + (if (options.enable_checksum) @as(usize, 4) else @as(usize, 0));
        buffer = try allocator.realloc(buffer, full_total_size);
        pos = header_end;
        buffer[pos] = 0x01; pos += 1; // type full (no checksum flag yet)
        @memcpy(buffer[pos..pos + new_data.len], new_data);
        pos += new_data.len;
        pattern_counts = utils.OpCodeCounts{};
    }

    // Set checksum flag (bit 7) in compression_type byte and append checksum over new_data
    if (options.enable_checksum) {
        buffer[header_end] |= 0x80; // set bit 7 to indicate checksum present
        const checksum = utils.xxhash32(new_data);
        buffer[pos] = @truncate(checksum); pos += 1;
        buffer[pos] = @truncate(checksum >> 8); pos += 1;
        buffer[pos] = @truncate(checksum >> 16); pos += 1;
        buffer[pos] = @truncate(checksum >> 24); pos += 1;
    }

    const actual_size = pos;

    if (actual_size < initial_total_size) {
        buffer = try allocator.realloc(buffer, actual_size);
    }

    stats.* = .{
        .old_size = old_data.len,
        .new_size = new_data.len,
        .delta_size = actual_size,
        .compression_ratio = if (new_data.len > 0) @as(f64, @floatFromInt(actual_size)) / @as(f64, @floatFromInt(new_data.len)) else 0.0,
        .change_density = 0.0,
        .compression_type = if (final_used_rle) "RLE" else "FullReplace",
        .used_rle = final_used_rle,
        .op_code_counts = pattern_counts,
    };
    return buffer;
}

fn estimateRLESizeForSpan(xor_slice: []const u8) usize {
    var size: usize = 0;
    var i: usize = 0;
    const len = xor_slice.len;
    while (i < len) {
        const is_zero = xor_slice[i] == 0;
        var run_len: usize = 1;
        while (i + run_len < len and (xor_slice[i + run_len] == 0) == is_zero) run_len += 1;
        size += 1 + get7BitEncodedSize(run_len);
        if (!is_zero) size += run_len;
        i += run_len;
    }
    return size;
}

fn checkUniform(xor_data: []const u8, start: usize, unit: usize, msk: u32, reps: usize) bool {
    const popc = popCount32(msk);
    if (popc == 0) return true;
    var first: [32]u8 = undefined;
    var idx: usize = 0;
    var i: usize = 0;
    while (i < unit) : (i += 1) {
        if ((msk & bit_masks[i]) != 0) {
            first[idx] = xor_data[start + i];
            idx += 1;
        }
    }
    var r: usize = 1;
    while (r < reps) : (r += 1) {
        idx = 0;
        i = 0;
        while (i < unit) : (i += 1) {
            if ((msk & bit_masks[i]) != 0) {
                if (xor_data[start + r * unit + i] != first[idx]) return false;
                idx += 1;
            }
        }
    }
    return true;
}
