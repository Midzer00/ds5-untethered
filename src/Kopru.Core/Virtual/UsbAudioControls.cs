// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// USB Audio Class 1 requests to a virtual pad's feature units (volume, mute) and streaming endpoints (sampling rate):
// SET_CUR is kept, GET_CUR answers it; MIN/MAX/RES describe a 0 to -96 dB volume in 1 dB steps. A sampling rate never set
// is the endpoint's own (rateFor: wIndex of the request, i.e. the endpoint address, to Hz). A request in the wrong
// direction (a SET read, a GET written) stalls.
internal sealed class UsbAudioControls(Func<ushort, int> rateFor)
{
	private readonly object gate = new();

	private readonly Dictionary<(byte Request, ushort Value, ushort Index), byte[]> controls = [];

	public SubmitResult Request(byte requestType, byte request, ushort value, ushort index, byte[] data, int length)
	{
		if (((requestType & 0x80) != 0) != ((request & 0x80) != 0))
		{
			return SubmitResult.Stall();
		}
		lock (gate)
		{
			if (request == 0x01)
			{
				controls[(request, value, index)] = data;
				return SubmitResult.Ok(data.Length);
			}
			byte[] answer = request switch
			{
				0x81 => controls.GetValueOrDefault(((byte)0x01, value, index)) ?? Default(index, length),
				0x82 => length == 2 ? [0x00, 0xA0] : Default(index, length),
				0x83 => length == 2 ? [0x00, 0x00] : Default(index, length),
				0x84 => length == 2 ? [0x00, 0x01] : Default(index, length),
				_ => []
			};
			return answer.Length == 0 ? SubmitResult.Stall() : SubmitResult.In(answer[..Math.Min(length, answer.Length)]);
		}
	}

	private byte[] Default(ushort index, int length)
	{
		if (length != 3)
		{
			return new byte[length];
		}
		int rate = rateFor(index);
		return [(byte)rate, (byte)(rate >> 8), (byte)(rate >> 16)];
	}
}
