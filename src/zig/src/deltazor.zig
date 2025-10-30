const std = @import("std");
const encoder = @import("encoder.zig");
const decoder = @import("decoder.zig");
const utils = @import("utils.zig");

pub const DeltaZor = struct {
    pub const Options = utils.Options;
    pub const Stats = utils.Stats;
    pub const OpCodeCounts = utils.OpCodeCounts;

    pub fn createDelta(old_data: []const u8, new_data: []const u8, allocator: std.mem.Allocator, options: Options) ![]u8 {
        var stats: Stats = undefined;
        return encoder.createDeltaWithStats(old_data, new_data, allocator, options, &stats);
    }

    pub fn calculateChangeDensity(old_data: []const u8, new_data: []const u8) f64 {
        return encoder.calculateChangeDensity(old_data, new_data);
    }

    pub fn applyDelta(old_data: []const u8, delta: []const u8, output: []u8, allocator: std.mem.Allocator) !void {
        return decoder.applyDelta(old_data, delta, output, allocator);
    }
};