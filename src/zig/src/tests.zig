const std = @import("std");
const json = std.json;
const crypto = std.crypto;
const Sha256 = crypto.hash.sha2.Sha256;
const mem = std.mem;
const testing = std.testing;
const fs = std.fs;
const fmt = std.fmt;

const deltazor = @import("deltazor.zig");

const TestDataManifest = struct {
    tests: []ManifestEntry,

    pub const ManifestEntry = struct {
        testId: u32,
        name: []const u8,
        category: []const u8,
        tags: ?[]const []const u8,
        baseFile: []const u8,
        nextFile: []const u8,
        deltaFile: []const u8,
        baseSize: usize,
        nextSize: usize,
        deltaSize: usize,
        baseChecksum: []const u8,
        nextChecksum: []const u8,
        deltaChecksum: []const u8,
        compressionRatio: f64,
        isValid: bool = true,
    };
};

fn computeSha256Hex(data: []const u8, allocator: mem.Allocator) ![]u8 {
    var hasher = Sha256.init(.{});
    hasher.update(data);
    const digest = hasher.finalResult();
    var hex_buf: [64]u8 = undefined;
    const hex = try fmt.bufPrint(&hex_buf, "{x}", .{digest});
    return allocator.dupe(u8, hex);
}

test "apply" {
    var gpa = std.heap.GeneralPurposeAllocator(.{}){};
    defer _ = gpa.deinit();
    const allocator = gpa.allocator();

    const manifest_path = "testdata/manifest.json";
    const manifest_file = try fs.cwd().openFile(manifest_path, .{ .mode = .read_only });
    defer manifest_file.close();

    const json_source = try manifest_file.readToEndAlloc(allocator, 1024 * 1024);
    defer allocator.free(json_source);

    const parse_options = json.ParseOptions{ .ignore_unknown_fields = true, .allocate = .alloc_always };
    var parsed = try json.parseFromSlice(TestDataManifest, allocator, json_source, parse_options);
    defer parsed.deinit();

    const manifest = parsed.value;

    for (manifest.tests) |entry| {
        if (!entry.isValid) continue;

        const base_path_str = try std.fmt.allocPrint(allocator, "testdata/{s}", .{entry.baseFile});
        defer allocator.free(base_path_str);
        const base_path = try fs.cwd().openFile(base_path_str, .{ .mode = .read_only });
        defer base_path.close();
        const base_bytes = try base_path.readToEndAlloc(allocator, entry.baseSize);
        defer allocator.free(base_bytes);

        const base_hash = try computeSha256Hex(base_bytes, allocator);
        defer allocator.free(base_hash);
        try testing.expectEqualStrings(entry.baseChecksum, base_hash);

        const next_path_str = try std.fmt.allocPrint(allocator, "testdata/{s}", .{entry.nextFile});
        defer allocator.free(next_path_str);
        const next_path = try fs.cwd().openFile(next_path_str, .{ .mode = .read_only });
        defer next_path.close();
        const next_bytes = try next_path.readToEndAlloc(allocator, entry.nextSize);
        defer allocator.free(next_bytes);

        const next_hash = try computeSha256Hex(next_bytes, allocator);
        defer allocator.free(next_hash);
        try testing.expectEqualStrings(entry.nextChecksum, next_hash);

        const delta_path_str = try std.fmt.allocPrint(allocator, "testdata/{s}", .{entry.deltaFile});
        defer allocator.free(delta_path_str);
        const delta_path = try fs.cwd().openFile(delta_path_str, .{ .mode = .read_only });
        defer delta_path.close();
        const expected_delta = try delta_path.readToEndAlloc(allocator, entry.deltaSize);
        defer allocator.free(expected_delta);

        const expected_delta_hash = try computeSha256Hex(expected_delta, allocator);
        defer allocator.free(expected_delta_hash);
        try testing.expectEqualStrings(entry.deltaChecksum, expected_delta_hash);

        const output = try allocator.alloc(u8, entry.nextSize);
        defer allocator.free(output);

        try deltazor.DeltaZor.applyDelta(base_bytes, expected_delta, output, allocator);

        try testing.expectEqualSlices(u8, output, next_bytes);
    }
}

test "create delta" {
    var gpa = std.heap.GeneralPurposeAllocator(.{}){};
    defer _ = gpa.deinit();
    const allocator = gpa.allocator();

    const manifest_path = "testdata/manifest.json";
    const manifest_file = try fs.cwd().openFile(manifest_path, .{ .mode = .read_only });
    defer manifest_file.close();

    const json_source = try manifest_file.readToEndAlloc(allocator, 1024 * 1024);
    defer allocator.free(json_source);

    const parse_options = json.ParseOptions{ .ignore_unknown_fields = true, .allocate = .alloc_always };
    var parsed = try json.parseFromSlice(TestDataManifest, allocator, json_source, parse_options);
    defer parsed.deinit();

    const manifest = parsed.value;

    for (manifest.tests) |entry| {
        if (!entry.isValid) continue;

        const base_path_str = try std.fmt.allocPrint(allocator, "testdata/{s}", .{entry.baseFile});
        defer allocator.free(base_path_str);
        const base_path = try fs.cwd().openFile(base_path_str, .{ .mode = .read_only });
        defer base_path.close();
        const base_bytes = try base_path.readToEndAlloc(allocator, entry.baseSize);
        defer allocator.free(base_bytes);

        const base_hash = try computeSha256Hex(base_bytes, allocator);
        defer allocator.free(base_hash);
        try testing.expectEqualStrings(entry.baseChecksum, base_hash);

        const next_path_str = try std.fmt.allocPrint(allocator, "testdata/{s}", .{entry.nextFile});
        defer allocator.free(next_path_str);
        const next_path = try fs.cwd().openFile(next_path_str, .{ .mode = .read_only });
        defer next_path.close();
        const next_bytes = try next_path.readToEndAlloc(allocator, entry.nextSize);
        defer allocator.free(next_bytes);

        const next_hash = try computeSha256Hex(next_bytes, allocator);
        defer allocator.free(next_hash);
        try testing.expectEqualStrings(entry.nextChecksum, next_hash);

        const delta_path_str = try std.fmt.allocPrint(allocator, "testdata/{s}", .{entry.deltaFile});
        defer allocator.free(delta_path_str);
        const delta_path = try fs.cwd().openFile(delta_path_str, .{ .mode = .read_only });
        defer delta_path.close();
        const expected_delta = try delta_path.readToEndAlloc(allocator, entry.deltaSize);
        defer allocator.free(expected_delta);

        const expected_delta_hash = try computeSha256Hex(expected_delta, allocator);
        defer allocator.free(expected_delta_hash);
        try testing.expectEqualStrings(entry.deltaChecksum, expected_delta_hash);

        const computed_delta = try deltazor.DeltaZor.createDelta(base_bytes, next_bytes, allocator, .{});
        defer allocator.free(computed_delta);

        try testing.expectEqual(entry.deltaSize, computed_delta.len);
        try testing.expectEqualSlices(u8, expected_delta, computed_delta);
    }
}

test "round trip" {
    var gpa = std.heap.GeneralPurposeAllocator(.{}){};
    defer _ = gpa.deinit();
    const allocator = gpa.allocator();

    const manifest_path = "testdata/manifest.json";
    const manifest_file = try fs.cwd().openFile(manifest_path, .{ .mode = .read_only });
    defer manifest_file.close();

    const json_source = try manifest_file.readToEndAlloc(allocator, 1024 * 1024);
    defer allocator.free(json_source);

    const parse_options = json.ParseOptions{ .ignore_unknown_fields = true, .allocate = .alloc_always };
    var parsed = try json.parseFromSlice(TestDataManifest, allocator, json_source, parse_options);
    defer parsed.deinit();

    const manifest = parsed.value;

    for (manifest.tests) |entry| {
        if (!entry.isValid) continue;

        const base_path_str = try std.fmt.allocPrint(allocator, "testdata/{s}", .{entry.baseFile});
        defer allocator.free(base_path_str);
        const base_path = try fs.cwd().openFile(base_path_str, .{ .mode = .read_only });
        defer base_path.close();
        const base_bytes = try base_path.readToEndAlloc(allocator, entry.baseSize);
        defer allocator.free(base_bytes);

        const base_hash = try computeSha256Hex(base_bytes, allocator);
        defer allocator.free(base_hash);
        try testing.expectEqualStrings(entry.baseChecksum, base_hash);

        const next_path_str = try std.fmt.allocPrint(allocator, "testdata/{s}", .{entry.nextFile});
        defer allocator.free(next_path_str);
        const next_path = try fs.cwd().openFile(next_path_str, .{ .mode = .read_only });
        defer next_path.close();
        const next_bytes = try next_path.readToEndAlloc(allocator, entry.nextSize);
        defer allocator.free(next_bytes);

        const next_hash = try computeSha256Hex(next_bytes, allocator);
        defer allocator.free(next_hash);
        try testing.expectEqualStrings(entry.nextChecksum, next_hash);

        const computed_delta = try deltazor.DeltaZor.createDelta(base_bytes, next_bytes, allocator, .{});
        defer allocator.free(computed_delta);

        const output = try allocator.alloc(u8, entry.nextSize);
        defer allocator.free(output);

        try deltazor.DeltaZor.applyDelta(base_bytes, computed_delta, output, allocator);

        try testing.expectEqualSlices(u8, output, next_bytes);
    }
}