using System.Text;

namespace FontViewer;

public record FontGlyphData(HashSet<int> ValidCodepoints, Dictionary<int, string> Names);

/// <summary>
/// Reads glyph names from a TrueType font file by parsing the cmap and post tables.
/// </summary>
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
			return ParseFont(data);
		}
		catch
		{
			return new(new(), new());
		}
	}

	private static FontGlyphData ParseFont(byte[] data)
	{
		var tables = ReadTableDirectory(data);

		if (!tables.TryGetValue("cmap", out var cmapInfo))
			return new(new(), new());

		var unicodeToGlyph = ParseCmap(data, cmapInfo.Offset);
		var validCodepoints = new HashSet<int>(unicodeToGlyph.Keys);

		var names = new Dictionary<int, string>();
		if (tables.TryGetValue("post", out var postInfo))
		{
			var glyphToName = ParsePost(data, postInfo.Offset, postInfo.Length);
			foreach (var (unicode, glyphId) in unicodeToGlyph)
			{
				if (glyphToName.TryGetValue(glyphId, out var name) && !string.IsNullOrEmpty(name))
					names[unicode] = name;
			}
		}

		return new(validCodepoints, names);
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

	private static Dictionary<int, int> ParseCmap(byte[] data, uint tableOffset)
	{
		var map = new Dictionary<int, int>();
		int numSubtables = ReadUInt16(data, (int)tableOffset + 2);

		uint bestOffset = 0;
		int bestPriority = -1;

		for (int i = 0; i < numSubtables; i++)
		{
			int rec = (int)tableOffset + 4 + i * 8;
			int platformId = ReadUInt16(data, rec);
			int encodingId = ReadUInt16(data, rec + 2);
			uint subtableOff = ReadUInt32(data, rec + 4) + tableOffset;

			int priority = -1;
			if (platformId == 3 && encodingId == 10) priority = 3;      // Windows Unicode full
			else if (platformId == 0 && encodingId == 4) priority = 2;  // Unicode full
			else if (platformId == 3 && encodingId == 1) priority = 1;  // Windows Unicode BMP
			else if (platformId == 0) priority = 0;                     // Unicode any

			if (priority > bestPriority)
			{
				bestPriority = priority;
				bestOffset = subtableOff;
			}
		}

		if (bestOffset == 0) return map;

		int format = ReadUInt16(data, (int)bestOffset);
		if (format == 4) ParseCmapFormat4(data, (int)bestOffset, map);
		else if (format == 12) ParseCmapFormat12(data, (int)bestOffset, map);

		return map;
	}

	private static void ParseCmapFormat4(byte[] data, int offset, Dictionary<int, int> map)
	{
		int segCountX2 = ReadUInt16(data, offset + 6);
		int segCount = segCountX2 / 2;

		int endCodesOff = offset + 14;
		int startCodesOff = endCodesOff + segCountX2 + 2; // +2 for reservedPad
		int idDeltaOff = startCodesOff + segCountX2;
		int idRangeOff = idDeltaOff + segCountX2;

		for (int i = 0; i < segCount; i++)
		{
			int endCode = ReadUInt16(data, endCodesOff + i * 2);
			int startCode = ReadUInt16(data, startCodesOff + i * 2);
			int idDelta = ReadInt16(data, idDeltaOff + i * 2);
			int idRange = ReadUInt16(data, idRangeOff + i * 2);

			if (startCode == 0xFFFF) break;

			for (int c = startCode; c <= endCode; c++)
			{
				int glyphId;
				if (idRange == 0)
				{
					glyphId = (c + idDelta) & 0xFFFF;
				}
				else
				{
					int glyphIdAddr = idRangeOff + i * 2 + idRange + (c - startCode) * 2;
					glyphId = ReadUInt16(data, glyphIdAddr);
					if (glyphId != 0) glyphId = (glyphId + idDelta) & 0xFFFF;
				}

				if (glyphId != 0)
					map[c] = glyphId;
			}
		}
	}

	private static void ParseCmapFormat12(byte[] data, int offset, Dictionary<int, int> map)
	{
		int numGroups = (int)ReadUInt32(data, offset + 12);

		for (int i = 0; i < numGroups; i++)
		{
			int groupOff = offset + 16 + i * 12;
			int startCharCode = (int)ReadUInt32(data, groupOff);
			int endCharCode = (int)ReadUInt32(data, groupOff + 4);
			int startGlyphId = (int)ReadUInt32(data, groupOff + 8);

			for (int c = startCharCode; c <= endCharCode; c++)
			{
				int glyphId = startGlyphId + (c - startCharCode);
				if (glyphId != 0)
					map[c] = glyphId;
			}
		}
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
