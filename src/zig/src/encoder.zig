const std = @import("std");
const utils = @import("utils.zig");

const bit_masks = utils.bit_masks;
const popCount32 = utils.popCount32;
const get7BitEncodedSize = utils.get7BitEncodedSize;
const write7BitEncodedIntDirect = utils.write7BitEncodedIntDirect;

pub const MotifCandidate = struct {
    unit_size: usize,
    repeat_length: usize,
    covered_length: usize,
    mask: u32,
    is_uniform: bool,
    is_full: bool,
};

const motif_unit_sizes = [_]usize{4, 8, 2, 3, 5, 6, 7};
const motif_probe_count: usize = 7;
const motif_density_threshold: f32 = 0.7;
const motif_savings_threshold: f32 = -0.5;
const motif_min_streak: usize = 2;
const max_motif_streak: usize = 50;


fn writeXORDelta(old_data: []const u8, new_data: []const u8, output: []u8, start: usize, length: usize, options: utils.Options) void {
    _ = options;
    var i: usize = 0;
    while (i < length) : (i += 1) {
        output[i] = old_data[start + i] ^ new_data[start + i];
    }
}

fn encodeXorWithMotifsDirect(xor_data: []const u8, buffer: []u8, data_pos: *usize, options: utils.Options, counts: *utils.OpCodeCounts) void {
    var pos: usize = 0;
    var temp_buffer: [4096]u8 = undefined;
    var pos_list: [32]usize = undefined;
    while (pos < xor_data.len) {
        if (options.enable_motif_detection) {
            if (findMotifCandidate(xor_data, pos, options)) |candidate| {
                const c = candidate;
                const is_uniform = c.is_uniform;
                const is_full = c.is_full;
                const msk = c.mask;
                const unit = c.unit_size;
                const reps = c.repeat_length;
                const changed = if (is_full) unit else popCount32(msk);
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
                const data_len = changed * (if (is_uniform) 1 else reps);
                if (data_len > temp_buffer.len) @panic("temp buffer too small for motif data");
                if (is_full) {
                    @memcpy(temp_buffer[0..data_len], xor_data[pos..pos + data_len]);
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
                    const max_rr = if (is_uniform) 1 else reps;
                    var rr: usize = 0;
                    while (rr < max_rr) : (rr += 1) {
                        var jj: usize = 0;
                        while (jj < changed) : (jj += 1) {
                            const local_pos = pos_list[jj];
                            temp_buffer[cursor] = xor_data[pos + rr * unit + local_pos];
                            cursor += 1;
                        }
                    }
                }
                @memcpy(buffer[data_pos.*..data_pos.* + data_len], temp_buffer[0..data_len]);
                data_pos.* += data_len;
                // update counts
                const density = if (is_full) 1.0 else @as(f32, @floatFromInt(changed)) / @as(f32, @floatFromInt(unit));
                const total_motif = counts.uniform_motif_count + counts.varying_motif_count + 1;
                const new_avg = if (total_motif > 0) (counts.average_mask_density * @as(f32, @floatFromInt(total_motif - 1)) + density) / @as(f32, @floatFromInt(total_motif)) else 0.0;
                if (is_uniform) {
                    counts.uniform_motif_count += 1;
                } else {
                    counts.varying_motif_count += 1;
                }
                counts.average_mask_density = new_avg;
                pos += c.covered_length;
                continue;
            }
        }
        // fallback basic RLE
        const is_zero = xor_data[pos] == 0;
        var run_len: usize = 1;
        while (pos + run_len < xor_data.len and (xor_data[pos + run_len] == 0) == is_zero) run_len += 1;
            const opcode = if (is_zero) utils.RLE_ZERO_RUN else utils.RLE_NON_ZERO_RUN;
        buffer[data_pos.*] = opcode;
        data_pos.* += 1;
        write7BitEncodedIntDirect(buffer, data_pos, run_len);
        if (!is_zero) {
            if (run_len > temp_buffer.len) @panic("temp buffer too small for run data");
            @memcpy(temp_buffer[0..run_len], xor_data[pos..pos + run_len]);
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
    if (used_rle and rle_data_len > new_data.len * 3 / 2) {
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

fn findMotifCandidate(xor_data: []const u8, start_pos: usize, options: utils.Options) ?MotifCandidate {
    _ = options;
    const len = xor_data.len - start_pos;
    var u: usize = 0;
    while (u < motif_probe_count) : (u += 1) {
        const unit_size = motif_unit_sizes[u];
        const max_possible_repeat = len / unit_size;
        if (max_possible_repeat < motif_min_streak) continue;

        var mask: u32 = 0;
        var pop: usize = 0;
        var ii: usize = 0;
        while (ii < unit_size) : (ii += 1) {
            if (xor_data[start_pos + ii] != 0) {
                mask |= bit_masks[ii];
                pop += 1;
            }
        }
        if (pop == 0) continue;

        const is_full = pop == unit_size;
        const density = if (unit_size > 0) @as(f32, @floatFromInt(pop)) / @as(f32, @floatFromInt(unit_size)) else 0.0;
        if (!is_full and density >= motif_density_threshold) continue;

        var repeat_len: usize = 1;
        var is_uniform: bool = undefined;

        if (is_full) {
            // full mode
            const first_unit_start = start_pos;
            const first_unit_end = start_pos + unit_size;
            var r: usize = 1;
            while (r < max_possible_repeat) : (r += 1) {
                const this_start = start_pos + r * unit_size;
                const this_end = this_start + unit_size;
                if (this_end > xor_data.len) break;
                if (!std.mem.eql(u8, xor_data[this_start..this_end], xor_data[first_unit_start..first_unit_end])) break;
                repeat_len += 1;
            }
            repeat_len = @min(repeat_len, max_motif_streak);
            is_uniform = true;
        } else {
            // masked mode
            var r: usize = 1;
            while (r < max_possible_repeat) : (r += 1) {
                var matches = true;
                var i: usize = 0;
                while (i < unit_size) : (i += 1) {
                    const val = xor_data[start_pos + r * unit_size + i];
                    const bit = bit_masks[i];
                    if ((mask & bit) != 0) {
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
                if (!matches) break;
                repeat_len += 1;
            }
            repeat_len = @min(repeat_len, max_motif_streak);
            is_uniform = checkUniform(xor_data, start_pos, unit_size, mask, repeat_len);
        }

        if (repeat_len < motif_min_streak) continue;

        const covered = repeat_len * unit_size;
        var header_size: usize = 1 + 1 + get7BitEncodedSize(repeat_len) + get7BitEncodedSize(unit_size);
        const changed_count = if (is_full) unit_size else pop;
        if (!is_full) {
            header_size += get7BitEncodedSize(@as(usize, @intCast(mask)));
        }
        const data_size = changed_count * (if (is_uniform) 1 else repeat_len);
        const motif_size = header_size + data_size;
        const rle_size = estimateRLESizeForSpan(xor_data[start_pos..start_pos + covered]);
        const savings = if (rle_size > 0) (@as(f32, @floatFromInt(rle_size)) - @as(f32, @floatFromInt(motif_size))) / @as(f32, @floatFromInt(rle_size)) else 0.0;
        if (savings > motif_savings_threshold) {
            return MotifCandidate{
                .unit_size = unit_size,
                .repeat_length = repeat_len,
                .covered_length = covered,
                .mask = mask,
                .is_uniform = is_uniform,
                .is_full = is_full,
            };
        }
    }
    return null;
}