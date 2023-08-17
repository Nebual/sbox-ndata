# [NData](https://asset.party/wiremod/ndata)

A substitute for `[ServerRPC]`, NData provides an abstraction for Client -> Server data transfer, using ConCommands as a transport.

## Usage

```csharp
// on client
bytes[] payload = FileSystem.Data.ReadAllBytes( "kittens.jpg" ).ToArray();
NData.Client.SendToServer( "eventname", payload );


// on server
[Event( "ndata.received.eventname")]
static void OnReceived( IClient client, byte[] payload )
{
	Log.Info( $"Received {payload.Length} bytes from {client.Name}" );
	using ( Stream s = FileSystem.Data.OpenWrite( "out.jpg" ) )
		s.Write( payload );
}
```

The payload will be compressed, split into 373 byte chunks, base64 encoded, and sent to the server using throttled ConCommands (33/s = 11kb/s).
The server will reassemble the payload, decompress it, and then emit the event.

## Server to Client

Just use `[ClientRPC]`, its much faster and supports 1mb payloads.

## Used By

- [SandboxPlus](https://github.com/Nebual/sandbox-plus) - for uploading Duplicator dupe files to the server
