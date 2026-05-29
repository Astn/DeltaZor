const std = @import("std");

pub const bit_masks = blk: {
    var arr: [32]u32 = undefined;
    for (0..32) |k| {
        arr[k] = (@as(u32, 1) << @intCast(k));
    }
    break :blk arr;
};

pub fn xxhash32(data: []const u8) u32 {
    return std.hash.XxHash32.hash(0, data);
}

pub fn readByte(reader_pos: *usize, data: []const u8) !u8 {
    if (reader_pos.* >= data.len) return error.EOF;
    const b = data[reader_pos.*];
    reader_pos.* += 1;
    return b;
}

pub fn read7bit(reader_pos: *usize, data: []const u8) !usize {
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

pub fn popCount32(m: u32) usize {
    var count: usize = 0;
    var x = m;
    while (x != 0) {
        count += 1;
        x &= x - 1;
    }
    return count;
}

pub fn get7BitEncodedSize(value: usize) usize {
    if (value < 128) return 1;
    var size: usize = 1;
    var v = value >> 7;
    while (v > 0) {
        size += 1;
        v >>= 7;
    }
    return size;
}

pub fn write7BitEncodedIntDirect(buffer: []u8, pos: *usize, value: usize) void {
    var v = value;
    while (v >= 128) {
        buffer[pos.*] = @as(u8, @intCast((v & 0x7F) | 0x80));
        pos.* += 1;
        v >>= 7;
    }
    buffer[pos.*] = @as(u8, @intCast(v));
    pos.* += 1;
}

pub const Options = struct {
    compression_threshold: f64 = 1.5,
    enable_checksum: bool = false,
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
    float_pattern_count: usize = 0,
};

pub const RLE_ZERO_RUN: u8 = 0x00;
pub const RLE_NON_ZERO_RUN: u8 = 0x01;
pub const RLE_EXTENSION: u8 = 0x02;
pub const RLE_TRUNCATION: u8 = 0x03;
pub const RLE_UNIFORM_MOTIF_REPEAT: u8 = 0x04;
pub const RLE_VARYING_MOTIF_REPEAT: u8 = 0x05;
pub const RLE_FLOAT_RUN: u8 = 0x06;