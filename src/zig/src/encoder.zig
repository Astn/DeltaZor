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

fn createRLEDeltaDirect(old_data: []const u8, new_data: []const u8, buffer: []u8, data_pos: *usize, options: utils.Options, counts: *utils.OpCodeCounts) void {
    const min_len = @min(old_data.len, new_data.len);
    var temp_buffer: [4096]u8 = undefined;

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
