const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const lib = b.addStaticLibrary(.{
        .name = "deltazor",
        .root_source_file = b.path("src/deltazor.zig"),
        .target = target,
        .optimize = optimize,
    });

    b.installArtifact(lib);

    const tests = b.addTest(.{
        .root_source_file = b.path("src/tests.zig"),
        .target = target,
        .optimize = optimize,
    });
    const run_tests = b.addRunArtifact(tests);
    b.step("test", "Run Zig tests", .{
        .run = run_tests,
    });
}