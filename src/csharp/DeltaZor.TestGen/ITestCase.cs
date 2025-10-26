namespace DZ.TestGen;

public interface ITestCase
{
    int Id { get; }
    string Name { get; }
    ReadOnlyMemory<byte> GenerateBase();
    ReadOnlyMemory<byte> GenerateNext();
    int ExpectedDeltaSize { get; }
    string[] Tags { get; }
    string? Description { get; }
}