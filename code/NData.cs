using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Sandbox;

// A substitute for [ServerRPC], NData provides an abstraction for Client -> Server data transfer, using ConCommands as a transport.
// Simply call NData.Client.SendToServer( "eventname", payload ), and listen for the event using:
//     [Event( "ndata.received.eventname")]
//     static void OnReceived( IClient client, byte[] payload )
// The payload will be compressed, split into 373 byte chunks, base64 encoded, and sent to the server using throttled ConCommands (33/s).
// The server will reassemble the payload, decompress it, and emit the event.
//
// For Server -> Client comms, just use [ClientRPC], its much faster and supports 1mb payloads
namespace NData
{
	public static partial class Client
	{
		public static int SendRate = 30; // 30ms between packets, to avoid "too many concommands"

		public static void SendToServer( string eventId, byte[] payload )
		{
			Game.AssertClient();
			var preparedPayload = PreparePayload( eventId, payload );
			EnqueuePackets( preparedPayload );
			ProcessQueue();
		}

		static byte[] PreparePayload( string topic, byte[] data )
		{
			using ( var stream = new MemoryStream() )
			{
				using ( var writer = new BinaryWriter( stream ) )
				{
					var compressed = CompressionHelper.Compress( data );
					if ( compressed.Length < data.Length )
					{
						writer.Write( (byte)PayloadFormat.CompressedDeflateBytes );
						writer.Write( topic );
						writer.Write( compressed );
					}
					else
					{
						writer.Write( (byte)PayloadFormat.Bytes );
						writer.Write( topic );
						writer.Write( data );
					}
					return stream.ToArray();
				}
			}
		}

		private static byte _lastPayloadId = 0;
		private static byte NewPayloadId()
		{
			_lastPayloadId++;
			if ( _lastPayloadId >= byte.MaxValue )
				_lastPayloadId = 0; // restarting is probably fine, nobody should have > 255 concurrent undelivered payloads
			return _lastPayloadId;
		}

		private const int MaxPacketSizeUnencoded = 507 - 2; // Concommand name is 2 bytes long
		private const int PacketHeaderSize = 5; // Payload header is 5 bytes long (1 payloadId + 2 packetId + 2 packetCount)
		internal static int MaxPacketSize = (int)(Math.Floor( MaxPacketSizeUnencoded / 4f ) * 3) - PacketHeaderSize; // base64 is 4 bytes per 3 bytes of data (rounded down)
		static void EnqueuePackets( byte[] data )
		{
			var payloadId = NewPayloadId();
			var packetCount = (ushort)Math.Ceiling( data.Length / (float)MaxPacketSize );
			// Log.Info( $"EnqueuePackets: payloadId: {payloadId} MaxPacketSize {MaxPacketSize} packetCount {packetCount} based on {data.Length}" );

			for ( ushort i = 0; i < packetCount; i++ )
			{
				var packetLength = Math.Min( MaxPacketSize, data.Length - (i * MaxPacketSize) );
				var packetData = new byte[packetLength + 5];
				packetData[0] = payloadId;
				BitConverter.GetBytes( i ).CopyTo( packetData, 1 );
				BitConverter.GetBytes( packetCount ).CopyTo( packetData, 3 );
				Array.Copy( data, i * MaxPacketSize, packetData, 5, packetLength );

				SendQueue.Enqueue( packetData );
			}
		}

		private static Queue<byte[]> SendQueue = new();
		private static bool IsQueueProcessing = false;

		private static async void ProcessQueue()
		{
			if ( IsQueueProcessing ) return;
			IsQueueProcessing = true;
			while ( SendQueue.Count > 0 )
			{
				var packet = SendQueue.Dequeue();
				Server.ReceivePacket( System.Convert.ToBase64String( packet ) );
				await Task.Delay( SendRate );
			}
			IsQueueProcessing = false;
		}

		internal enum PayloadFormat : byte
		{
			Bytes = 1,
			CompressedDeflateBytes = 2
		}
	}

	public static partial class Server
	{
		private static Dictionary<ushort, Dictionary<ushort, byte[]>> ServerReceiveBuffers = new();

		[ConCmd.Server( "ND" )]
		public static void ReceivePacket( string rawPacket )
		{
			Game.AssertServer();
			var packet = System.Convert.FromBase64String( rawPacket );
			var payloadId = packet[0];
			var packetId = BitConverter.ToUInt16( packet, 1 );
			var packetCount = BitConverter.ToUInt16( packet, 3 );
			var packetData = new byte[packet.Length - 5];
			Array.Copy( packet, 5, packetData, 0, packetData.Length );

			// Log.Info( $"ND: ID: {payloadId} packetId: {packetId} packetCount: {packetCount} Length: {packetData.Length} data: {System.Convert.ToBase64String( packetData )}" );

			var buffer = ServerReceiveBuffers.GetOrCreate( payloadId );
			buffer.Add( packetId, packetData );

			if ( buffer.Count == packetCount )
			{
				var fullData = new byte[(packetCount - 1) * Client.MaxPacketSize + packetData.Length];
				for ( ushort i = 0; i < packetCount; i++ )
				{
					buffer[i].CopyTo( fullData, i * Client.MaxPacketSize );
				}
				ServerReceiveBuffers.Remove( payloadId );

				EmitPayloadEvent( fullData );
			}
		}

		static void EmitPayloadEvent( byte[] data )
		{
			using ( var stream = new MemoryStream( data ) )
			{
				using ( var reader = new BinaryReader( stream ) )
				{
					var format = (Client.PayloadFormat)reader.ReadByte();
					var topic = reader.ReadString();
					byte[] payload;
					if ( format == Client.PayloadFormat.Bytes )
					{
						payload = reader.ReadBytes( (int)(stream.Length - stream.Position) );
					}
					else if ( format == Client.PayloadFormat.CompressedDeflateBytes )
					{
						payload = CompressionHelper.Decompress( stream );
					}
					else
					{
						throw new Exception( "NData.EmitPayloadEvent: Invalid payload format" );
					}

					Log.Info( $"NData.EmitPayloadEvent: Emitting event ndata.received.{topic}: {payload.Length} bytes" );
					Event.Run( $"ndata.received.{topic}", ConsoleSystem.Caller, payload );
				}
			}
		}
	}


	public static class CompressionHelper
	{
		public static byte[] Compress( byte[] data )
		{
			byte[] compressArray = null;

			try
			{
				using ( var memoryStream = new MemoryStream() )
				{
					using ( var deflateStream = new DeflateStream( memoryStream, CompressionLevel.Optimal ) )
					{
						deflateStream.Write( data, 0, data.Length );
					}

					compressArray = memoryStream.ToArray();
				}
			}
			catch ( Exception e )
			{
				Log.Warning( $"CompressionHelper.Compress failed {e}" );
			}

			return compressArray;
		}

		public static byte[] Decompress( byte[] data )
		{
			using ( var compressStream = new MemoryStream( data ) )
			{
				return Decompress( compressStream );
			}
		}

		public static byte[] Decompress( Stream compressStream )
		{
			byte[] decompressedArray = null;

			try
			{
				using ( var decompressedStream = new MemoryStream() )
				{
					using ( var deflateStream = new DeflateStream( compressStream, CompressionMode.Decompress ) )
					{
						deflateStream.CopyTo( decompressedStream );
					}

					decompressedArray = decompressedStream.ToArray();
				}
			}
			catch ( Exception e )
			{
				Log.Warning( $"CompressionHelper.Decompress failed {e}" );
			}

			return decompressedArray;
		}
	}
}
