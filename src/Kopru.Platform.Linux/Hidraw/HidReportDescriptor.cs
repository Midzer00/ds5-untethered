// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Platform.Linux.Hidraw;

public enum HidReportKind
{
	Input,
	Output,
	Feature
}

// The report sizes a HID report descriptor declares, from the item format of the public "Device Class Definition for
// HID 1.11" (section 6.2.2): short items (a prefix byte with size, type and tag, then 0, 1, 2 or 4 data bytes) and long
// items (prefix 0xFE). Only what the bridge needs: the size of each report by kind and report ID, and the usage of the
// first application collection (a gamepad is usage page 0x01, usage 0x05).
public sealed class HidReportDescriptor
{
	private const int GlobalUsagePage = 0x0, GlobalReportSize = 0x7, GlobalReportId = 0x8, GlobalReportCount = 0x9,
		GlobalPush = 0xA, GlobalPop = 0xB;
	private const int MainInput = 0x8, MainOutput = 0x9, MainCollection = 0xA, MainFeature = 0xB, MainEndCollection = 0xC;
	private const int LocalUsage = 0x0;
	private const int CollectionApplication = 0x01;

	private readonly Dictionary<(HidReportKind Kind, int Id), long> bits;

	private HidReportDescriptor(Dictionary<(HidReportKind, int), long> bits, int usagePage, int usage)
	{
		this.bits = bits;
		UsagePage = usagePage;
		Usage = usage;
	}

	// Of the first application collection; 0 when there is none.
	public int UsagePage { get; }

	public int Usage { get; }

	public bool IsGamepad => UsagePage == 0x01 && Usage == 0x05;

	// True when the device numbers its reports (every report then starts with its ID byte).
	public bool UsesReportIds => bits.Keys.Any(k => k.Id != 0);

	public IEnumerable<int> ReportIds(HidReportKind kind) => bits.Keys.Where(k => k.Kind == kind).Select(k => k.Id).Order();

	// Bytes of one report as hidraw passes it: the data plus the report ID byte when the device uses IDs. Null when the
	// descriptor declares no such report.
	public int? ReportLength(HidReportKind kind, int reportId) =>
		bits.TryGetValue((kind, reportId), out long b) ? (int)((b + 7) / 8) + (reportId != 0 ? 1 : 0) : null;

	// The longest report of a kind (as Windows reports it in HIDP_CAPS: with the ID byte); 0 when there is none.
	public int MaxLength(HidReportKind kind) =>
		bits.Keys.Where(k => k.Kind == kind).Select(k => ReportLength(kind, k.Id) ?? 0).DefaultIfEmpty(0).Max();

	public static HidReportDescriptor Parse(ReadOnlySpan<byte> descriptor)
	{
		Dictionary<(HidReportKind, int), long> bits = [];
		Stack<(long Size, long Count, int Id, int Page)> stack = new();
		long reportSize = 0, reportCount = 0;
		int reportId = 0, usagePage = 0, depth = 0;
		int firstPage = 0, firstUsage = 0;
		bool haveApplication = false;
		uint? lastUsage = null;
		int i = 0;
		while (i < descriptor.Length)
		{
			byte prefix = descriptor[i];
			if (prefix == 0xFE)
			{
				// Long item: data size, tag, data. None of them matter here.
				if (i + 1 >= descriptor.Length)
				{
					break;
				}
				i += 3 + descriptor[i + 1];
				continue;
			}
			int size = (prefix & 0x3) == 3 ? 4 : prefix & 0x3;
			int type = (prefix >> 2) & 0x3;
			int tag = prefix >> 4;
			if (i + 1 + size > descriptor.Length)
			{
				break;
			}
			uint data = 0;
			for (int b = 0; b < size; b++)
			{
				data |= (uint)descriptor[i + 1 + b] << (8 * b);
			}
			i += 1 + size;
			switch (type)
			{
				case 0:
					switch (tag)
					{
						case MainInput:
							Add(bits, HidReportKind.Input, reportId, reportSize * reportCount);
							break;
						case MainOutput:
							Add(bits, HidReportKind.Output, reportId, reportSize * reportCount);
							break;
						case MainFeature:
							Add(bits, HidReportKind.Feature, reportId, reportSize * reportCount);
							break;
						case MainCollection:
							if (depth == 0 && data == CollectionApplication && !haveApplication)
							{
								haveApplication = true;
								// A 4-byte usage carries its own page in the high 16 bits.
								firstPage = lastUsage is { } u && u > 0xFFFF ? (int)(u >> 16) : usagePage;
								firstUsage = lastUsage is { } v ? (int)(v & 0xFFFF) : 0;
							}
							depth++;
							break;
						case MainEndCollection:
							depth = Math.Max(0, depth - 1);
							break;
					}
					// Local items apply to the next main item only.
					lastUsage = null;
					break;
				case 1:
					switch (tag)
					{
						case GlobalUsagePage:
							usagePage = (int)data;
							break;
						case GlobalReportSize:
							reportSize = data;
							break;
						case GlobalReportId:
							reportId = (int)data;
							break;
						case GlobalReportCount:
							reportCount = data;
							break;
						case GlobalPush:
							stack.Push((reportSize, reportCount, reportId, usagePage));
							break;
						case GlobalPop:
							if (stack.Count > 0)
							{
								(reportSize, reportCount, reportId, usagePage) = stack.Pop();
							}
							break;
					}
					break;
				case 2:
					if (tag == LocalUsage)
					{
						lastUsage = size == 4 ? data : data & 0xFFFF;
					}
					break;
			}
		}
		return new HidReportDescriptor(bits, firstPage, firstUsage);
	}

	private static void Add(Dictionary<(HidReportKind, int), long> bits, HidReportKind kind, int id, long count)
	{
		bits[(kind, id)] = bits.GetValueOrDefault((kind, id)) + count;
	}
}
