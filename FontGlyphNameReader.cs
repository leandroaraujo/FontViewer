using System.Text;
using SkiaSharp;

namespace FontViewer;

public record FontGlyphData(HashSet<int> ValidCodepoints, Dictionary<int, string> Names);

public static class FontGlyphNameReader
{
	public static async Task<FontGlyphData> ReadFontDataAsync(string fontFileName)
	{
		try
		{
			using var stream = await FileSystem.OpenAppPackageFileAsync(fontFileName);
			using var ms = new MemoryStream();
			await stream.CopyToAsync(ms);
			var data = ms.ToArray();

			using var skStream = new SKMemoryStream(data);
			using var typeface = SKTypeface.FromStream(skStream);

			if (typeface == null)
				return new([], new());

			var (validCodepoints, codepointToGlyphId) = DetectValidGlyphs(typeface);
			var names = ExtractGlyphNames(data, codepointToGlyphId);

			return new(validCodepoints, names);
		}
		catch
		{
			return new([], new());
		}
	}

	private static (HashSet<int> valid, Dictionary<int, ushort> cpToGlyph) DetectValidGlyphs(SKTypeface typeface)
	{
		var valid = new HashSet<int>();
		var cpToGlyph = new Dictionary<int, ushort>();

		// Map each BMP codepoint individually for reliable detection
		for (int cp = 0x0020; cp <= 0xFFFF; cp++)
		{
			if (cp >= 0xD800 && cp <= 0xDFFF) continue;
			ushort glyphId = typeface.GetGlyph(cp);
			if (glyphId != 0)
			{
				valid.Add(cp);
				cpToGlyph[cp] = glyphId;
			}
		}

		return (valid, cpToGlyph);
	}

	private static Dictionary<int, string> ExtractGlyphNames(byte[] data, Dictionary<int, ushort> cpToGlyph)
	{
		var tables = ReadTableDirectory(data);
		if (!tables.TryGetValue("post", out var postInfo))
			return new();

		var glyphToName = ParsePost(data, postInfo.Offset, postInfo.Length);
		var names = new Dictionary<int, string>();

		foreach (var (cp, glyphId) in cpToGlyph)
		{
			if (glyphToName.TryGetValue(glyphId, out var name) && !string.IsNullOrEmpty(name))
				names[cp] = name;
		}

		return names;
	}

	private static Dictionary<string, (uint Offset, uint Length)> ReadTableDirectory(byte[] data)
	{
		var tables = new Dictionary<string, (uint, uint)>();
		int numTables = ReadUInt16(data, 4);

		for (int i = 0; i < numTables; i++)
		{
			int rec = 12 + i * 16;
			string tag = Encoding.ASCII.GetString(data, rec, 4);
			uint offset = ReadUInt32(data, rec + 8);
			uint length = ReadUInt32(data, rec + 12);
			tables[tag] = (offset, length);
		}

		return tables;
	}

	private static Dictionary<int, string> ParsePost(byte[] data, uint offset, uint length)
	{
		var names = new Dictionary<int, string>();
		uint version = ReadUInt32(data, (int)offset);

		if (version != 0x00020000) // Only format 2.0 has glyph names
			return names;

		int numGlyphs = ReadUInt16(data, (int)offset + 32);
		var nameIndices = new int[numGlyphs];

		for (int i = 0; i < numGlyphs; i++)
			nameIndices[i] = ReadUInt16(data, (int)offset + 34 + i * 2);

		// Read Pascal strings (extra names beyond the 258 standard Mac names)
		int stringDataStart = (int)offset + 34 + numGlyphs * 2;
		int tableEnd = (int)(offset + length);
		var extraNames = new List<string>();
		int pos = stringDataStart;

		while (pos < tableEnd && pos < data.Length)
		{
			int len = data[pos];
			pos++;
			if (pos + len > data.Length) break;
			extraNames.Add(Encoding.ASCII.GetString(data, pos, len));
			pos += len;
		}

		for (int i = 0; i < numGlyphs; i++)
		{
			int idx = nameIndices[i];
			if (idx >= 258)
			{
				int extraIdx = idx - 258;
				if (extraIdx < extraNames.Count)
					names[i] = extraNames[extraIdx];
			}
		}

		return names;
	}

	private static ushort ReadUInt16(byte[] data, int offset) =>
		(ushort)((data[offset] << 8) | data[offset + 1]);

	private static short ReadInt16(byte[] data, int offset) =>
		(short)((data[offset] << 8) | data[offset + 1]);

	private static uint ReadUInt32(byte[] data, int offset) =>
		(uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
