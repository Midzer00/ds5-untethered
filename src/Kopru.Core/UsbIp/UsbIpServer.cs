// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Kopru.Core.UsbIp;

// A device served over USB/IP. Submit may complete at once or later (an interrupt IN waits for input); Unlink cancels a
// pending submit and returns whether it was still pending (then its completion must never be sent).
public interface IUsbIpDevice
{
	UsbIpDeviceInfo Info { get; }

	void Submit(SubmitCommand command, Action<SubmitResult> complete);

	bool Unlink(uint sequence);

	// The client let go of the device (connection closed).
	void Released();
}

// Serves one device on the loopback interface (only this computer can connect): answers the device list and the import,
// then relays URBs between the client (usbip-win2's driver) and the device.
public sealed class UsbIpServer : IAsyncDisposable
{
	private readonly IUsbIpDevice device;

	private readonly TcpListener listener;

	private readonly CancellationTokenSource stop = new();

	private Task? acceptLoop;

	public UsbIpServer(IUsbIpDevice device, int port = 0)
	{
		this.device = device;
		listener = new TcpListener(IPAddress.Loopback, port);
	}

	public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

	// Raised when a client imported the device / when that connection ended.
	public event Action? Imported;

	public event Action? Released;

	// False once the listening socket died (or the server was disposed).
	public bool IsAlive => acceptLoop is { IsCompleted: false };

	public void Start()
	{
		listener.Start();
		acceptLoop = AcceptLoopAsync();
	}

	public async ValueTask DisposeAsync()
	{
		stop.Cancel();
		listener.Stop();
		if (acceptLoop != null)
		{
			try
			{
				await acceptLoop;
			}
			catch (Exception)
			{
				// Shutting down.
			}
		}
	}

	private async Task AcceptLoopAsync()
	{
		while (!stop.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await listener.AcceptTcpClientAsync(stop.Token);
			}
			catch (Exception) when (stop.IsCancellationRequested)
			{
				return;
			}
			_ = ServeAsync(client);
		}
	}

	private async Task ServeAsync(TcpClient client)
	{
		using TcpClient owned = client;
		client.NoDelay = true;
		NetworkStream stream = client.GetStream();
		SemaphoreSlim writeGate = new(1, 1);
		bool imported = false;
		try
		{
			byte[] header = await UsbIpWire.ReadExactlyAsync(stream, 8, stop.Token);
			ushort code = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
			if (code == UsbIpCodes.OpReqDevlist)
			{
				byte[] count = new byte[4];
				BinaryPrimitives.WriteUInt32BigEndian(count, 1);
				await stream.WriteAsync((byte[])[.. UsbIpWire.OpHeader(UsbIpCodes.OpRepDevlist, 0), .. count, .. device.Info.Encode(withInterfaces: true)], stop.Token);
				return;
			}
			if (code != UsbIpCodes.OpReqImport)
			{
				return;
			}
			string busId = Encoding.ASCII.GetString(await UsbIpWire.ReadExactlyAsync(stream, 32, stop.Token)).TrimEnd('\0');
			if (busId != device.Info.BusId)
			{
				await stream.WriteAsync(UsbIpWire.OpHeader(UsbIpCodes.OpRepImport, 1), stop.Token);
				return;
			}
			await stream.WriteAsync((byte[])[.. UsbIpWire.OpHeader(UsbIpCodes.OpRepImport, 0), .. device.Info.Encode(withInterfaces: false)], stop.Token);
			imported = true;
			Imported?.Invoke();

			ConcurrentDictionary<uint, SubmitCommand> pending = new();
			while (!stop.IsCancellationRequested)
			{
				object command = await UsbIpWire.ReadCommandAsync(stream, stop.Token);
				if (command is SubmitCommand submit)
				{
					pending[submit.Sequence] = submit;
					device.Submit(submit, result =>
					{
						if (pending.TryRemove(submit.Sequence, out _))
						{
							_ = SendAsync(stream, writeGate, UsbIpWire.RetSubmit(submit, result));
						}
					});
				}
				else if (command is UnlinkCommand unlink)
				{
					bool cancelled = pending.TryRemove(unlink.TargetSequence, out _) && device.Unlink(unlink.TargetSequence);
					await SendAsync(stream, writeGate, UsbIpWire.RetUnlink(unlink, cancelled ? UsbIpCodes.ConnectionReset : 0));
				}
			}
		}
		catch (Exception)
		{
			// The client went away (detach, shutdown) or sent something unreadable: the connection ends.
		}
		finally
		{
			if (imported)
			{
				device.Released();
				Released?.Invoke();
			}
		}
	}

	private static async Task SendAsync(NetworkStream stream, SemaphoreSlim gate, byte[] message)
	{
		await gate.WaitAsync();
		try
		{
			await stream.WriteAsync(message);
		}
		catch (Exception)
		{
			// The read loop notices the closed connection.
		}
		finally
		{
			gate.Release();
		}
	}
}
