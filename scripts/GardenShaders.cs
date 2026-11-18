using System;
using System.IO;

internal static class GardenShaders
{
    private const string Path = "res://assets/abyssal/shaders.bin";
    private static readonly Lazy<Library> Data = new(Load);

    private sealed record Library(byte[] Density, Program[][] Profiles);
    public sealed record Program(string Name, byte[] Vertex, byte[] Fragment);

    public static byte[] Density => Data.Value.Density;
    public static Program[] ForQuality(int quality) => Data.Value.Profiles[quality];

    private static Library Load()
    {
        byte[] asset = Godot.FileAccess.GetFileAsBytes(Path);
        if (asset.Length == 0)
            throw new FileNotFoundException("The Abyssal Garden shader library is missing.", Path);
        using var reader = new BinaryReader(new MemoryStream(asset, writable: false));
        if (reader.ReadUInt32() != 0x53594241) // ABYS
            throw new InvalidDataException("Unrecognized Abyssal Garden shader library.");
        var code = new byte[reader.ReadInt32()][];
        for (int i = 0; i < code.Length; i++)
            code[i] = reader.ReadBytes(reader.ReadInt32());
        byte[] density = code[reader.ReadInt32()];
        int count = reader.ReadInt32();
        Program[][] profiles = [new Program[count], new Program[count], new Program[count]];
        for (int i = 0; i < count; i++)
        {
            string name = reader.ReadString();
            foreach (var profile in profiles)
                profile[i] = new(name, code[reader.ReadInt32()], code[reader.ReadInt32()]);
        }
        return new(density, profiles);
    }
}
