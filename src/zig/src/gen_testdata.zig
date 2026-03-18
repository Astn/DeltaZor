/// Regenerates test delta files using the current Zig encoder.
/// Run with: zig run src/gen_testdata.zig --dep deltazor -- testdata
const std = @import("std");
const deltazor = @import("deltazor.zig");

pub fn main() !void {
    var gpa = std.heap.GeneralPurposeAllocator(.{}){};
    defer _ = gpa.deinit();
    const allocator = gpa.allocator();

    const args = try std.process.argsAlloc(allocator);
    defer std.process.argsFree(allocator, args);

    const testdata_dir = if (args.len > 1) args[1] else "testdata";

    // Read manifest to get test IDs and file names
    const manifest_path = try std.fmt.allocPrint(allocator, "{s}/manifest.json", .{testdata_dir});
    defer allocator.free(manifest_path);

    const cwd = std.fs.cwd();
    const manifest_file = try cwd.openFile(manifest_path, .{ .mode = .read_only });
    defer manifest_file.close();
    const manifest_json = try manifest_file.readToEndAlloc(allocator, 10 * 1024 * 1024);
    defer allocator.free(manifest_json);

    // Parse JSON to extract test entries
    const Parsed = struct {
        tests: []struct {
            testId: u32,
            baseFile: []const u8,
            nextFile: []const u8,
            deltaFile: []const u8,
            baseSize: usize,
            nextSize: usize,
        },
    };
    const parse_opts = std.json.ParseOptions{ .ignore_unknown_fields = true, .allocate = .alloc_always };
    var parsed = try std.json.parseFromSlice(Parsed, allocator, manifest_json, parse_opts);
    defer parsed.deinit();

    for (parsed.value.tests) |entry| {
        const base_path = try std.fmt.allocPrint(allocator, "{s}/{s}", .{ testdata_dir, entry.baseFile });
        defer allocator.free(base_path);
        const next_path = try std.fmt.allocPrint(allocator, "{s}/{s}", .{ testdata_dir, entry.nextFile });
        defer allocator.free(next_path);
        const delta_path = try std.fmt.allocPrint(allocator, "{s}/{s}", .{ testdata_dir, entry.deltaFile });
        defer allocator.free(delta_path);

        const base_file = try cwd.openFile(base_path, .{ .mode = .read_only });
        defer base_file.close();
        const base_bytes = try base_file.readToEndAlloc(allocator, entry.baseSize);
        defer allocator.free(base_bytes);

        const next_file = try cwd.openFile(next_path, .{ .mode = .read_only });
        defer next_file.close();
        const next_bytes = try next_file.readToEndAlloc(allocator, entry.nextSize);
        defer allocator.free(next_bytes);

        const delta = try deltazor.DeltaZor.createDelta(base_bytes, next_bytes, allocator, .{});
        defer allocator.free(delta);

        const delta_file = try cwd.createFile(delta_path, .{});
        defer delta_file.close();
        try delta_file.writeAll(delta);

        std.debug.print("Test {d}: generated delta {s} ({d} bytes)\n", .{ entry.testId, entry.deltaFile, delta.len });
    }
    std.debug.print("Done regenerating deltas.\n", .{});
}
