const std = @import("std");
const utils = @import("utils.zig");

const readByte = utils.readByte;
const read7bit = utils.read7bit;
const crc32 = utils.crc32;
const popCount32 = utils.popCount32;
const bit_masks = utils.bit_masks;

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
                    else => return error.InvalidOpcode,
                }
            }

            if (pos != output.len) return error.InvalidLength;
        },
        else => return error.UnknownCompression,
    }
}