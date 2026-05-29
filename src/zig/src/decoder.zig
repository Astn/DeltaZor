const std = @import("std");
const utils = @import("utils.zig");

const readByte = utils.readByte;
const read7bit = utils.read7bit;
const xxhash32 = utils.xxhash32;
const popCount32 = utils.popCount32;
const bit_masks = utils.bit_masks;

pub fn applyDelta(old_data: []const u8, delta: []const u8, output: []u8, allocator: std.mem.Allocator) !void {
    _ = allocator;

    // Minimum valid delta: 5 bytes (4 output_length + 1 compression_type)
    if (delta.len < 5) return error.InvalidDelta;

    var header_pos: usize = 0;
    const b0 = delta[header_pos]; header_pos += 1;
    const b1 = delta[header_pos]; header_pos += 1;
    const b2 = delta[header_pos]; header_pos += 1;
    const b3 = delta[header_pos]; header_pos += 1;
    const output_len: u32 = @as(u32, b0) | (@as(u32, b1) << 8) | (@as(u32, b2) << 16) | (@as(u32, b3) << 24);
    if (output.len < @as(usize, output_len)) return error.OutputTooSmall;

    const comp_type_byte: u8 = delta[header_pos]; header_pos += 1;

    // Self-describing checksum: bit 7 of compression_type indicates checksum present
    const has_checksum = (comp_type_byte & 0x80) != 0;
    const comp_type: u8 = comp_type_byte & 0x7F;

    const data_start: usize = header_pos;
    const checksum_size: usize = if (has_checksum) 4 else 0;
    if (delta.len < data_start + checksum_size) return error.InvalidDelta;
    const data_end: usize = delta.len - checksum_size;
    const data = delta[data_start..data_end];

    // Read expected checksum from the last 4 bytes (before decoding)
    var expected_crc: u32 = 0;
    if (has_checksum) {
        const checksum_start: usize = delta.len - 4;
        const cb0 = delta[checksum_start];
        const cb1 = delta[checksum_start + 1];
        const cb2 = delta[checksum_start + 2];
        const cb3 = delta[checksum_start + 3];
        expected_crc = @as(u32, cb0) | (@as(u32, cb1) << 8) | (@as(u32, cb2) << 16) | (@as(u32, cb3) << 24);
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
                const opcode = try readByte(&reader_pos, data);
                switch (opcode) {
                    utils.RLE_ZERO_RUN => { // zero run
                        const count = try read7bit(&reader_pos, data);
                        pos += count;
                        if (pos > output.len) return error.Invalid;
                    },
                    utils.RLE_NON_ZERO_RUN => { // non zero run
                        const count = try read7bit(&reader_pos, data);
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
                    utils.RLE_EXTENSION => { // extension
                        const count = try read7bit(&reader_pos, data);
                        if (pos + count > output.len) return error.Invalid;
                        const ext_start = reader_pos;
                        reader_pos += count;
                        if (reader_pos > data.len) return error.EOF;
                        @memcpy(output[pos..pos + count], data[ext_start..ext_start + count]);
                        pos += count;
                    },
                    utils.RLE_TRUNCATION => { // truncation
                        const trunc_len = try read7bit(&reader_pos, data);
                        pos = trunc_len;
                    },
                    utils.RLE_UNIFORM_MOTIF_REPEAT => { // uniform motif
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
                    utils.RLE_VARYING_MOTIF_REPEAT => { // varying motif
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
                    utils.RLE_FLOAT_RUN => { // float run 0x06
                        // [flags][laneCount:7bit][bitmap: ceil(laneCount/8)][packed: 4*changedLanes]
                        // Mirrors C# Encoder.cs TryEmitFloatRun (source of truth).
                        const flags = try readByte(&reader_pos, data);
                        if (flags != 0x00) return error.Invalid; // reserved flags must be zero
                        const lane_count = try read7bit(&reader_pos, data);
                        if (lane_count < 2) return error.Invalid;
                        const lane_size: usize = 4;
                        const span = lane_count * lane_size;
                        if (pos + span > output.len) return error.Invalid;

                        const bitmap_bytes = (lane_count + 7) / 8;
                        const bitmap_start = reader_pos;
                        reader_pos += bitmap_bytes;
                        if (reader_pos > data.len) return error.EOF;
                        const bitmap = data[bitmap_start..reader_pos];

                        var l: usize = 0;
                        while (l < lane_count) : (l += 1) {
                            const bit = (bitmap[l >> 3] & (@as(u8, 1) << @intCast(l & 7))) != 0;
                            if (!bit) continue;
                            const lane_start = reader_pos;
                            reader_pos += lane_size;
                            if (reader_pos > data.len) return error.EOF;
                            const base_off = pos + l * lane_size;
                            output[base_off] ^= data[lane_start];
                            output[base_off + 1] ^= data[lane_start + 1];
                            output[base_off + 2] ^= data[lane_start + 2];
                            output[base_off + 3] ^= data[lane_start + 3];
                        }
                        pos += span;
                    },
                    utils.RLE_HALF_RUN => { // half run 0x07
                        // [flags][laneCount:7bit][bitmap: ceil(laneCount/8)][packed: 2*changedLanes]
                        // Mirrors C# Encoder.cs TryEmitHalfRun (source of truth).
                        const flags = try readByte(&reader_pos, data);
                        if (flags != 0x00) return error.Invalid; // reserved flags must be zero
                        const lane_count = try read7bit(&reader_pos, data);
                        if (lane_count < 2) return error.Invalid;
                        const lane_size: usize = 2;
                        const span = lane_count * lane_size;
                        if (pos + span > output.len) return error.Invalid;

                        const bitmap_bytes = (lane_count + 7) / 8;
                        const bitmap_start = reader_pos;
                        reader_pos += bitmap_bytes;
                        if (reader_pos > data.len) return error.EOF;
                        const bitmap = data[bitmap_start..reader_pos];

                        var l: usize = 0;
                        while (l < lane_count) : (l += 1) {
                            const bit = (bitmap[l >> 3] & (@as(u8, 1) << @intCast(l & 7))) != 0;
                            if (!bit) continue;
                            const lane_start = reader_pos;
                            reader_pos += lane_size;
                            if (reader_pos > data.len) return error.EOF;
                            const base_off = pos + l * lane_size;
                            output[base_off] ^= data[lane_start];
                            output[base_off + 1] ^= data[lane_start + 1];
                        }
                        pos += span;
                    },
                    utils.RLE_CHANNEL_RUN => { // channel run 0x08
                        // [flags][stride:1][channelMask: ceil(stride/8)][unitCount:7bit][packed: popcount(mask)*unitCount]
                        // Mirrors C# Encoder.cs TryEmitChannelRun (source of truth).
                        const flags = try readByte(&reader_pos, data);
                        if (flags != 0x00) return error.Invalid; // reserved flags must be zero
                        const stride = try readByte(&reader_pos, data);
                        if (stride < 1) return error.Invalid;
                        const channel_mask_bytes = (@as(usize, stride) + 7) / 8;
                        const mask_start = reader_pos;
                        reader_pos += channel_mask_bytes;
                        if (reader_pos > data.len) return error.EOF;
                        const channel_mask = data[mask_start..reader_pos];
                        const unit_count = try read7bit(&reader_pos, data);
                        if (unit_count < 2) return error.Invalid;
                        const span = unit_count * @as(usize, stride);
                        if (pos + span > output.len) return error.Invalid;

                        var u: usize = 0;
                        while (u < unit_count) : (u += 1) {
                            const base_off = pos + u * @as(usize, stride);
                            var c: usize = 0;
                            while (c < stride) : (c += 1) {
                                if ((channel_mask[c >> 3] & (@as(u8, 1) << @intCast(c & 7))) == 0) continue;
                                const b = try readByte(&reader_pos, data);
                                output[base_off + c] ^= b;
                            }
                        }
                        pos += span;
                    },
                    utils.RLE_ARITHMETIC => { // global arithmetic 0x09
                        // [elemWidth:1][step: elemWidth bytes LE][laneCount:7bit]
                        // Mirrors C# Encoder.cs TryEmitGlobalArithmetic (source of truth). Adds the
                        // step (mod 2^(8*elemWidth), wraparound) into each LE integer lane of output
                        // (pre-filled with old) — additive, NOT XOR.
                        const elem_width = try readByte(&reader_pos, data);
                        if (elem_width != 1 and elem_width != 2 and elem_width != 4 and elem_width != 8) return error.Invalid;
                        var step_buf: [8]u8 = undefined;
                        var b: usize = 0;
                        while (b < elem_width) : (b += 1) {
                            step_buf[b] = try readByte(&reader_pos, data);
                        }
                        const lane_count = try read7bit(&reader_pos, data);
                        if (lane_count < 2) return error.Invalid;
                        const span = lane_count * @as(usize, elem_width);
                        if (pos + span > output.len) return error.Invalid;

                        const width_mask: u64 = if (elem_width == 8) std.math.maxInt(u64) else (@as(u64, 1) << @intCast(8 * @as(usize, elem_width))) - 1;
                        var step: u64 = 0;
                        b = 0;
                        while (b < elem_width) : (b += 1) {
                            step |= @as(u64, step_buf[b]) << @intCast(8 * b);
                        }

                        var l: usize = 0;
                        while (l < lane_count) : (l += 1) {
                            const base_off = pos + l * @as(usize, elem_width);
                            var cur: u64 = 0;
                            b = 0;
                            while (b < elem_width) : (b += 1) {
                                cur |= @as(u64, output[base_off + b]) << @intCast(8 * b);
                            }
                            const res = (cur +% step) & width_mask;
                            b = 0;
                            while (b < elem_width) : (b += 1) {
                                output[base_off + b] = @truncate(res >> @intCast(8 * b));
                            }
                        }
                        pos += span;
                    },
                    utils.RLE_PLANAR => { // planar arithmetic 0x0A
                        // [planeCount:1][steps: planeCount bytes][unitCount:7bit]
                        // Mirrors C# Encoder.cs TryEmitPlanarArithmetic (source of truth). Adds
                        // steps[p] (mod 256, byte wraparound) into output[u*P+p] (pre-filled with
                        // old) — additive, NOT XOR.
                        const plane_count = try readByte(&reader_pos, data);
                        if (plane_count < 1 or plane_count > 8) return error.Invalid;
                        var steps: [8]u8 = undefined;
                        var c: usize = 0;
                        while (c < plane_count) : (c += 1) {
                            steps[c] = try readByte(&reader_pos, data);
                        }
                        const unit_count = try read7bit(&reader_pos, data);
                        if (unit_count < 2) return error.Invalid;
                        const span = unit_count * @as(usize, plane_count);
                        if (pos + span > output.len) return error.Invalid;

                        var u: usize = 0;
                        while (u < unit_count) : (u += 1) {
                            const base_off = pos + u * @as(usize, plane_count);
                            c = 0;
                            while (c < plane_count) : (c += 1) {
                                output[base_off + c] = output[base_off + c] +% steps[c];
                            }
                        }
                        pos += span;
                    },
                    else => return error.InvalidOpcode,
                }
            }

            if (pos != output.len) return error.InvalidLength;
        },
        else => return error.UnknownCompression,
    }

    // Validate checksum AFTER decoding — checksum is over the decoded output (new_data)
    if (has_checksum) {
        const actual_crc = xxhash32(output[0..@as(usize, output_len)]);
        if (actual_crc != expected_crc) return error.ChecksumFailed;
    }
}