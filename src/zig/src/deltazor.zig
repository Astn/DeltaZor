const std = @import("std");
const mem = std.mem;

const bit_masks = blk: {
    var arr: [32]u32 = undefined;
    for (0..32) |k| {
        arr[k] = (@as(u32, 1) << @intCast(k));
    }
    break :blk arr;
};

fn crc32(data: []const u8) u32 {
    const poly: u32 = 0xEDB88320;
    var table: [256]u32 = undefined;
    var i: usize = 0;
    while (i < 256) : (i += 1) {
        var crc: u32 = @intCast(i);
        var j: u5 = 0;
        while (j < 8) : (j += 1) {
            crc = if ((crc & 1) != 0) ((crc >> 1) ^ poly) else (crc >> 1);
        }
        table[@as(usize, i)] = crc;
    }

    var crc: u32 = 0xFFFFFFFF;
    for (data) |b| {
        crc = table[(crc ^ @as(u32, b)) & 0xFF] ^ (crc >> 8);
    }
    return crc ^ 0xFFFFFFFF;
}

fn readByte(reader_pos: *usize, data: []const u8) !u8 {
    if (reader_pos.* >= data.len) return error.EOF;
    const b = data[reader_pos.*];
    reader_pos.* += 1;
    return b;
}

fn read7bit(reader_pos: *usize, data: []const u8) !usize {
    var value: u64 = 0;
    var shift: u6 = 0;
    while (true) {
        if (reader_pos.* >= data.len) return error.EOF;
        const b = data[reader_pos.*];
        reader_pos.* += 1;
        value |= (@as(u64, b & 0x7F) << shift);
        shift += 7;
        if (shift > 56) return error.Overflow;
        if ((b & 0x80) == 0) break;
    }
    return @intCast(value);
}

fn popCount32(m: u32) usize {
    var count: usize = 0;
    var x = m;
    while (x != 0) {
        count += 1;
        x &= x - 1;
    }
    return count;
}



pub const DeltaZor = struct {
    pub const Options = struct {
        compression_threshold: f64 = 0.95,
        enable_checksum: bool = true,
        max_stack_buffer_size: usize = 4096,
        use_simd: bool = true,
        enable_motif_detection: bool = true,
        motif_min_run_threshold: usize = 0,
    };

    pub const Stats = struct {
        old_size: usize,
        new_size: usize,
        delta_size: usize,
        compression_ratio: f64,
        change_density: f64,
        compression_type: []const u8,
        used_rle: bool,
        op_code_counts: OpCodeCounts,
    };

    pub const OpCodeCounts = struct {
        zero_run_count: usize = 0,
        non_zero_run_count: usize = 0,
        extension_count: usize = 0,
        truncation_count: usize = 0,
        uniform_motif_count: usize = 0,
        varying_motif_count: usize = 0,
        average_mask_density: f32 = 0.0,
    };

    const motif_unit_sizes = [_]usize{4, 8, 2, 3, 5, 6, 7};
    const motif_probe_count: usize = 7;
    const motif_density_threshold: f32 = 0.7;
    const motif_savings_threshold: f32 = -0.5;
    const motif_min_streak: usize = 2;
    const max_motif_streak: usize = 50;

    const MotifCandidate = struct {
        unit_size: usize,
        repeat_length: usize,
        covered_length: usize,
        mask: u32,
        is_uniform: bool,
        is_full: bool,
    };

    const RLE_ZERO_RUN: u8 = 0x00;
    const RLE_NON_ZERO_RUN: u8 = 0x01;
    const RLE_EXTENSION: u8 = 0x02;
    const RLE_TRUNCATION: u8 = 0x03;
    const RLE_UNIFORM_MOTIF_REPEAT: u8 = 0x04;
    const RLE_VARYING_MOTIF_REPEAT: u8 = 0x05;

    pub fn createDelta(old_data: []const u8, new_data: []const u8, allocator: std.mem.Allocator, options: Options) ![]u8 {
        var stats: Stats = undefined;
        return createDeltaWithStats(old_data, new_data, allocator, options, &stats);
    }

    pub fn calculateChangeDensity(old_data: []const u8, new_data: []const u8) f64 {
    const min_len = @min(old_data.len, new_data.len);
    var changes: usize = 0;
    var i: usize = 0;
    while (i < min_len) : (i += 1) {
        if (old_data[i] != new_data[i]) changes += 1;
    }
    changes += @abs(@as(isize, @intCast(old_data.len)) - @as(isize, @intCast(new_data.len)));
    return if (min_len > 0) @as(f64, @floatFromInt(changes)) / @as(f64, @floatFromInt(min_len)) else 1.0;
}



fn writeXORDelta(old_data: []const u8, new_data: []const u8, output: []u8, start: usize, length: usize, options: Options) void {
    _ = options;
    var i: usize = 0;
    while (i < length) : (i += 1) {
        output[i] = old_data[start + i] ^ new_data[start + i];
    }
}





fn encodeXorWithMotifsEstimate(xor_data: []const u8, options: DeltaZor.Options, counts: *DeltaZor.OpCodeCounts) usize {
    var pos: usize = 0;
    var estimated_size: usize = 0;
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
                const data_len = changed * (if (is_uniform) 1 else reps);
                estimated_size += 1; // opcode
                estimated_size += 1; // flags
                estimated_size += get7BitEncodedSize(reps);
                estimated_size += get7BitEncodedSize(unit);
                if (!is_full) {
                    estimated_size += get7BitEncodedSize(@as(usize, @intCast(msk)));
                }
                estimated_size += data_len;
                // update counts (same as write version)
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
        estimated_size += 1; // opcode
        estimated_size += get7BitEncodedSize(run_len);
        if (!is_zero) {
            estimated_size += run_len;
        }
        if (is_zero) {
            counts.zero_run_count += 1;
        } else {
            counts.non_zero_run_count += 1;
        }
        pos += run_len;
    }
    return estimated_size;
}

fn createRLEDeltaEstimate(old_data: []const u8, new_data: []const u8, options: DeltaZor.Options, counts: *DeltaZor.OpCodeCounts) usize {
    const min_len = @min(old_data.len, new_data.len);
    var temp_buffer: [4096]u8 = undefined;
    var estimated_size: usize = 0;

    const use_full_xor = min_len <= options.max_stack_buffer_size and options.enable_motif_detection;
    if (use_full_xor) {
        const xor_buffer = temp_buffer[0..min_len];
        writeXORDelta(old_data, new_data, xor_buffer, 0, min_len, options);
        estimated_size += encodeXorWithMotifsEstimate(xor_buffer, options, counts);
    } else {
        // Streaming basic RLE estimate
        var pos: usize = 0;
        while (pos < min_len) {
            const run_start = pos;
            const is_zero = old_data[pos] == new_data[pos];
            while (pos < min_len and (old_data[pos] == new_data[pos]) == is_zero) pos += 1;
            const run_len = pos - run_start;
            estimated_size += 1; // opcode
            estimated_size += get7BitEncodedSize(run_len);
            if (!is_zero) estimated_size += run_len;
            if (is_zero) counts.zero_run_count += 1 else counts.non_zero_run_count += 1;
        }
    }

    // Extension or truncation
    if (new_data.len > old_data.len) {
        const extension_len = new_data.len - old_data.len;
        estimated_size += 1; // opcode
        estimated_size += get7BitEncodedSize(extension_len);
        estimated_size += extension_len;
        counts.extension_count += 1;
    } else if (new_data.len < old_data.len) {
        estimated_size += 1; // opcode
        estimated_size += get7BitEncodedSize(new_data.len);
        counts.truncation_count += 1;
    }

    return estimated_size;
}

fn enhancedEstimateRLEWithMotifs(old_data: []const u8, new_data: []const u8, options: DeltaZor.Options) usize {
    var counts = DeltaZor.OpCodeCounts{};
    return createRLEDeltaEstimate(old_data, new_data, options, &counts);
}

fn write7BitEncodedIntDirect(buffer: []u8, pos: *usize, value: usize) void {
    var v = value;
    while (v >= 128) {
        buffer[pos.*] = @as(u8, @intCast((v & 0x7F) | 0x80));
        pos.* += 1;
        v >>= 7;
    }
    buffer[pos.*] = @as(u8, @intCast(v));
    pos.* += 1;
}

fn encodeXorWithMotifsDirect(xor_data: []const u8, buffer: []u8, data_pos: *usize, options: DeltaZor.Options, counts: *DeltaZor.OpCodeCounts) void {
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
                const opcode = if (is_uniform) RLE_UNIFORM_MOTIF_REPEAT else RLE_VARYING_MOTIF_REPEAT;
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
        const opcode = if (is_zero) RLE_ZERO_RUN else RLE_NON_ZERO_RUN;
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

fn createRLEDeltaDirect(old_data: []const u8, new_data: []const u8, buffer: []u8, data_pos: *usize, options: DeltaZor.Options, counts: *DeltaZor.OpCodeCounts) void {
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
            const opcode = if (is_zero) RLE_ZERO_RUN else RLE_NON_ZERO_RUN;
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
        buffer[data_pos.*] = RLE_EXTENSION;
        data_pos.* += 1;
        write7BitEncodedIntDirect(buffer, data_pos, extension_len);
        @memcpy(buffer[data_pos.*..data_pos.* + extension_len], extension);
        data_pos.* += extension_len;
        counts.extension_count += 1;
    } else if (new_data.len < old_data.len) {
        buffer[data_pos.*] = RLE_TRUNCATION;
        data_pos.* += 1;
        write7BitEncodedIntDirect(buffer, data_pos, new_data.len);
        counts.truncation_count += 1;
    }
}

pub fn createDeltaWithStats(old_data: []const u8, new_data: []const u8, allocator: std.mem.Allocator, options: Options, stats: *Stats) ![]u8 {
    const density = calculateChangeDensity(old_data, new_data);
    const length_diff = @abs(@as(isize, @intCast(old_data.len)) - @as(isize, @intCast(new_data.len)));
    const obvious_full = density > 0.95 and length_diff < @max(1, new_data.len / 10) and !options.enable_motif_detection;
    const used_rle = !obvious_full;

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
    buffer[pos] = if (used_rle) @as(u8, 0x00) else @as(u8, 0x01); pos += 1;
    const data_start = pos;

    var pattern_counts = OpCodeCounts{};
    if (used_rle) {
        createRLEDeltaDirect(old_data, new_data, buffer, &pos, options, &pattern_counts);
    } else {
        @memcpy(buffer[pos..pos + new_data.len], new_data);
        pos += new_data.len;
    }

    const rle_data_len = pos - data_start;
    var final_used_rle = used_rle;
    var final_data_len = rle_data_len;
    if (used_rle and rle_data_len > new_data.len * 3 / 2) {
        // Fallback to full replace
        final_used_rle = false;
        final_data_len = new_data.len;
        // Realloc to exact size for full
        const full_total_size = 9 + new_data.len;
        buffer = try allocator.realloc(buffer, full_total_size);
        pos = header_end;
        buffer[pos] = 0x01; pos += 1; // type full
        @memcpy(buffer[pos..pos + new_data.len], new_data);
        pos += new_data.len;
        pattern_counts = OpCodeCounts{};
    }

    const final_data_start = if (final_used_rle) data_start else header_end + 1;
    const checksum = if (options.enable_checksum) crc32(buffer[final_data_start..final_data_start + final_data_len]) else 0;

    // Write checksum
    const chk32 = checksum;
    buffer[pos] = @truncate(chk32); pos += 1;
    buffer[pos] = @truncate(chk32 >> 8); pos += 1;
    buffer[pos] = @truncate(chk32 >> 16); pos += 1;
    buffer[pos] = @truncate(chk32 >> 24); pos += 1;

    const actual_size = pos;

    if (actual_size < initial_total_size) {
        buffer = try allocator.realloc(buffer, actual_size);
    }

    stats.* = .{
        .old_size = old_data.len,
        .new_size = new_data.len,
        .delta_size = actual_size,
        .compression_ratio = if (new_data.len > 0) @as(f64, @floatFromInt(actual_size)) / @as(f64, @floatFromInt(new_data.len)) else 0.0,
        .change_density = density,
        .compression_type = if (final_used_rle) "RLE" else "FullReplace",
        .used_rle = final_used_rle,
        .op_code_counts = pattern_counts,
    };
    return buffer;
}

    pub fn applyDelta(old_data: []const u8, delta: []const u8, output: []u8, allocator: std.mem.Allocator) !void {
        _ = allocator;

        if (delta.len < 9) return error.InvalidDelta;

        var header_pos: usize = 0;
        const b0 = delta[header_pos]; header_pos += 1;
        const b1 = delta[header_pos]; header_pos += 1;
        const b2 = delta[header_pos]; header_pos += 1;
        const b3 = delta[header_pos]; header_pos += 1;
        const output_len: u32 = @as(u32, b0) | (@as(u32, b1) << 8) | (@as(u32, b2) << 16) | (@as(u32, b3) << 24);
        if (output.len < @as(usize, output_len)) return error.OutputTooSmall;

        const comp_type: u8 = delta[header_pos]; header_pos += 1;

        const data_start: usize = header_pos;
        const checksum_start: usize = delta.len - 4;
        const data = delta[data_start..checksum_start];

        var checksum_pos: usize = checksum_start;
        const cb0 = delta[checksum_pos]; checksum_pos += 1;
        const cb1 = delta[checksum_pos]; checksum_pos += 1;
        const cb2 = delta[checksum_pos]; checksum_pos += 1;
        const cb3 = delta[checksum_pos]; checksum_pos += 1;
        const expected_crc: u32 = @as(u32, cb0) | (@as(u32, cb1) << 8) | (@as(u32, cb2) << 16) | (@as(u32, cb3) << 24);

        if (expected_crc != 0) {
            const actual_crc = crc32(data);
            if (actual_crc != expected_crc) return error.ChecksumFailed;
        }

        switch (comp_type) {
            0x01 => { // full replace
                if (data.len != @as(usize, output_len)) return error.InvalidData;
                @memcpy(output[0..@as(usize, output_len)], data);
                if (output.len > @as(usize, output_len)) @memset(output[@as(usize, output_len)..], 0);
            },
            0x00 => { // RLE
                const min_len = @min(old_data.len, output.len);
                @memcpy(output[0..min_len], old_data[0..min_len]);
                if (output.len > old_data.len) @memset(output[old_data.len..], 0);

                var reader_pos: usize = 0;
                var pos: usize = 0;

                while (reader_pos < data.len) {
                    const opcode = readByte(&reader_pos, data) catch return error.InvalidDelta;
                    switch (opcode) {
                        0x00 => { // zero run
                            const count = read7bit(&reader_pos, data) catch return error.InvalidDelta;
                            pos += count;
                            if (pos > output.len) return error.Invalid;
                        },
                        0x01 => { // non zero run
                            const count = read7bit(&reader_pos, data) catch return error.InvalidDelta;
                            if (pos + count > output.len) return error.Invalid;
                            const xor_start = reader_pos;
                            reader_pos += count;
                            if (reader_pos > data.len) return error.EOF;
                            var i: usize = 0;
                            while (i < count) : (i += 1) {
                                output[pos + i] ^= data[xor_start + i];
                            }
                            pos += count;
                        },
                        0x02 => { // extension
                            const count = read7bit(&reader_pos, data) catch return error.InvalidDelta;
                            if (pos + count > output.len) return error.Invalid;
                            const ext_start = reader_pos;
                            reader_pos += count;
                            if (reader_pos > data.len) return error.EOF;
                            @memcpy(output[pos..pos + count], data[ext_start..ext_start + count]);
                            pos += count;
                        },
                        0x03 => { // truncation
                            const trunc_len = read7bit(&reader_pos, data) catch return error.InvalidDelta;
                            pos = trunc_len;
                        },
                        0x04 => { // uniform motif
                            const flags = try readByte(&reader_pos, data);
                            const repeat_length = try read7bit(&reader_pos, data);
                            if (repeat_length < 2) return error.Invalid;
                            const unit_size = try read7bit(&reader_pos, data);
                            if (unit_size < 1 or unit_size > 32) return error.Invalid;

                            const is_masked = (flags & 0x80) != 0;
                            var mask: u32 = 0;
                            var changed_count: usize = 0;
                            if (is_masked) {
                                const mask_int = try read7bit(&reader_pos, data);
                                mask = @intCast(mask_int);
                                changed_count = popCount32(mask);
                            } else {
                                changed_count = unit_size;
                                mask = 0;
                                var k: usize = 0;
                                while (k < unit_size) : (k += 1) {
                                    mask |= bit_masks[k];
                                }
                            }

                            const uniform_start = reader_pos;
                            reader_pos += changed_count;
                            if (reader_pos > data.len) return error.EOF;
                            const uniform_xor_data = data[uniform_start..reader_pos];

                            if (pos + unit_size * repeat_length > output.len) return error.Invalid;

                            var pos_list: [32]usize = undefined;
                            var c: usize = 0;
                            var ii: usize = 0;
                            while (ii < unit_size) : (ii += 1) {
                                const bit = bit_masks[ii];
                                if (!is_masked or (mask & bit) != 0) {
                                    pos_list[c] = ii;
                                    c += 1;
                                }
                            }

                            var rr: usize = 0;
                            while (rr < repeat_length) : (rr += 1) {
                                var jj: usize = 0;
                                while (jj < changed_count) : (jj += 1) {
                                    const local_pos = pos_list[jj];
                                    output[pos + rr * unit_size + local_pos] ^= uniform_xor_data[jj];
                                }
                            }
                            pos += unit_size * repeat_length;
                        },
                        0x05 => { // varying motif
                            const flags = try readByte(&reader_pos, data);
                            const repeat_length = try read7bit(&reader_pos, data);
                            if (repeat_length < 2) return error.Invalid;
                            const unit_size = try read7bit(&reader_pos, data);
                            if (unit_size < 1 or unit_size > 32) return error.Invalid;

                            const is_masked = (flags & 0x80) != 0;
                            var mask: u32 = 0;
                            var changed_count: usize = 0;
                            if (is_masked) {
                                const mask_int = try read7bit(&reader_pos, data);
                                mask = @intCast(mask_int);
                                changed_count = popCount32(mask);
                            } else {
                                changed_count = unit_size;
                                mask = 0;
                                var k: usize = 0;
                                while (k < unit_size) : (k += 1) {
                                    mask |= bit_masks[k];
                                }
                            }

                            const total_data_size = changed_count * repeat_length;
                            const varying_start = reader_pos;
                            reader_pos += total_data_size;
                            if (reader_pos > data.len) return error.EOF;
                            const all_xor_data = data[varying_start..reader_pos];

                            if (pos + unit_size * repeat_length > output.len) return error.Invalid;

                            var pos_list: [32]usize = undefined;
                            var c: usize = 0;
                            var ii: usize = 0;
                            while (ii < unit_size) : (ii += 1) {
                                const bit = bit_masks[ii];
                                if (!is_masked or (mask & bit) != 0) {
                                    pos_list[c] = ii;
                                    c += 1;
                                }
                            }

                            var data_cursor: usize = 0;
                            var rr: usize = 0;
                            while (rr < repeat_length) : (rr += 1) {
                                var jj: usize = 0;
                                while (jj < changed_count) : (jj += 1) {
                                    const local_pos = pos_list[jj];
                                    output[pos + rr * unit_size + local_pos] ^= all_xor_data[data_cursor + jj];
                                }
                                data_cursor += changed_count;
                            }
                            pos += unit_size * repeat_length;
                        },
                        else => return error.InvalidOpcode,
                    }
                }

                if (pos != output.len) return error.InvalidLength;
            },
            else => return error.UnknownCompression,
        }
    }
};

fn get7BitEncodedSize(value: usize) usize {
    if (value < 128) return 1;
    var size: usize = 1;
    var v = value >> 7;
    while (v > 0) {
        size += 1;
        v >>= 7;
    }
    return size;
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

fn findMotifCandidate(xor_data: []const u8, start_pos: usize, options: DeltaZor.Options) ?DeltaZor.MotifCandidate {
    _ = options;
    const len = xor_data.len - start_pos;
    var u: usize = 0;
    while (u < DeltaZor.motif_probe_count) : (u += 1) {
        const unit_size = DeltaZor.motif_unit_sizes[u];
        const max_possible_repeat = len / unit_size;
        if (max_possible_repeat < DeltaZor.motif_min_streak) continue;

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
        if (!is_full and density >= DeltaZor.motif_density_threshold) continue;

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
            repeat_len = @min(repeat_len, DeltaZor.max_motif_streak);
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
            repeat_len = @min(repeat_len, DeltaZor.max_motif_streak);
            is_uniform = checkUniform(xor_data, start_pos, unit_size, mask, repeat_len);
        }

        if (repeat_len < DeltaZor.motif_min_streak) continue;

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
        if (savings > DeltaZor.motif_savings_threshold) {
            return DeltaZor.MotifCandidate{
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

pub fn main() !void {
    var gpa = std.heap.GeneralPurposeAllocator(.{}){};
    defer _ = gpa.deinit();
    const allocator = gpa.allocator();

    const old_data = "Hello, World!".*;
    const new_data = "Hello, Zig!".*;

    const delta = try DeltaZor.createDelta(&old_data, &new_data, allocator, .{});
    defer allocator.free(delta);

    std.debug.print("Delta size: {d}\n", .{delta.len});

    const output = try allocator.alloc(u8, 12);
    defer allocator.free(output);

    try DeltaZor.applyDelta(output, delta, allocator);

    std.debug.print("Output: {s}\n", .{output});
}
