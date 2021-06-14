---
title: "AirDrop Anywhere - Part 4 - Making it work on Windows"
date: 2021-06-14T17:30:00Z
tags: [.net, networking, airdrop, apple]
images: [img/airdrop-anywhere-cover.jpg]
summary: "Part four of the journey towards implementing AirDrop on any platform using .NET Core. This episode we cover receiving files on a non-Apple device from an Apple device via an intermediary using .NET."
---

> This is part 4 of a series of posts:
> - [Part 1 - Introduction](/posts/2021-05-airdrop-anywhere-part-1)
> - [Part 2 - Writing some code](/posts/2021-05-airdrop-anywhere-part-2)
> - [Part 3 - Receiving files](/posts/2021-05-airdrop-anywhere-part-3)
> - [Part 4 - Making it work on Windows](/posts/2021-06-airdrop-anywhere-part-4)
>     - [GitHub (cross-platform bits)](https://github.com/deanward81/AirDropAnywhere/tree/2021-06-14-cross-platform-bits)
> - [GitHub (latest)](https://github.com/deanward81/AirDropAnywhere/tree/main) - **NOTE** still work in progress!

In [episode 3](/posts/2021-05-airdrop-anywhere-part-3) we worked through an implementation of AirDrop using C# that allows receiving files between Apple devices. In this episode we'll look at implementing the bits necessary to open this up to non-Apple devices.

## The Plan

Now that we can send files between Apple devices we're in a good position to start opening things up to non-Apple devices. However, as mentioned in [episode 1](/posts/2021-05-airdrop-anywhere-part-1), it is unlikely that a non-Apple device has hardware support for adhoc wireless connections between devices. This makes implementation of AirDrop _directly_ on non-Apple devices practically impossible without additional hardware. Instead we'll implement a proxy that can run on a platform with supported hardware (e.g. an Apple device or Linux device running [OWL](https://owlink.org)).

Initially I thought that splitting things into three projects was the sanest approach to building this - `AirDropAnywhere.Core` would contain the core parts implementing the AirDrop protocol with `AirDropAnywhere.Cli` containing the CLI components and `AirDropAnywhere.Web` containing a set of endpoints to support non-Apple devices. Instead I've decided to collapse hosting of  all server _and_ client pieces into `AirDropAnywhere.Cli` - this simplifies the build pipeline and provides a single executable that can act as either a client or a server to our AirDrop implementation. In practice this is implemented using the [command support in Spectre.Console](https://spectreconsole.net/cli/commands) meaning the system now looks something like this:

<img src="/img/airdrop-anywhere-9.png" width=480 alt="AirDrop Anywhere System Diagram"><br/>
<sub style="color:lightgray">AirDrop Anywhere System Diagram</sub>

Let's run through some of the key concepts in the design and then dig into implementation details...

## Peering Overview

In order to support non-AirDrop devices we need to have the concept of a "peer" - an arbitrary client that connects to our server and is made discoverable to AirDrop-compatible devices. This becomes a core abstraction in the code and the mechanism in which various subsystems communicate with each other. Say hello to `AirDropPeer`:

```c#
/// <summary>
/// Exposes a way for the AirDrop HTTP API to communicate with an arbitrary peer that does
/// not directly support the AirDrop protocol.  
/// </summary>
public abstract class AirDropPeer
{   
    /// <summary>
    /// Gets the unique identifier of this peer.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the (display) name of this peer.
    /// </summary>
    public string Name { get; protected set; }

    /// <summary>
    /// Determines whether the peer wants to receive files from a sender. 
    /// </summary>
    /// <param name="request">
    /// An <see cref="AskRequest"/> object representing information about the sender
    /// and the files that they wish to send.
    /// sender
    /// </param>
    /// <returns>
    /// <c>true</c> if the receiver wants to accept the file transfer, <c>false</c> otherwise.
    /// </returns>
    public abstract ValueTask<bool> CanAcceptFilesAsync(AskRequest request);
    
    /// <summary>
    /// Notifies the peer that a file has been uploaded. This method is for every
    /// file extracted from the archive sent by an AirDrop-compatible device.
    /// </summary>
    /// <param name="filePath">
    /// Path to an extracted file.
    /// </param>
    public abstract ValueTask OnFileUploadedAsync(string filePath);
}
```

This is fairly straightforward - there's a unique identifier for the peer, a display name and some methods to allow the AirDrop HTTP API to communicate with the peer. This is implemented as an abstract class rather than an interface because we need to manage the identifier in the framework - we use this as the host name in mDNS and AirDrop is particularly fussy about what characters it allows in the hostname. We simply use a random 12 character alpha-numeric identifier which satisfies AirDrop's requirements and means the implementor doesn't need to know about this detail.

An implementation of `AirDropPeer` is provided by the underlying peering mechanism - in our case we're allowing clients to connect using [SignalR](https://dotnet.microsoft.com/apps/aspnet/signalr) over Websockets so we provide an implementation that layers upon its messaging protocol. When a peer connects to our server we register it so that the core pieces in `AirDropAnywhere.Core` are aware of its presence. Similarly when the peer disconnects we unregister it. That functionality is exposed on [`AirDropService`](https://github.com/deanward81/AirDropAnywhere/blob/2021-06-14-cross-platform-bits/src/AirDropAnywhere.Core/AirDropService.cs):

```c#
/// <summary>
/// Registers an <see cref="AirDropPeer"/> so that it becomes discoverable to
/// AirDrop-compatible devices.
/// </summary>
/// <param name="peer">
/// An instance of <see cref="AirDropPeer"/>.
/// </param>
public ValueTask RegisterPeerAsync(AirDropPeer peer);

/// <summary>
/// Unregisters an <see cref="AirDropPeer"/> so that it is no longer discoverable by
/// AirDrop-compatible devices. If the peer is not registered then this operation is no-op.
/// </summary>
/// <param name="peer">
/// A previously registered instance of <see cref="AirDropPeer"/>.
/// </param>
public ValueTask UnregisterPeerAsync(AirDropPeer peer);

/// <summary>
/// Attempts to get an <see cref="AirDropPeer"/> by its unique identifier.
/// </summary>
/// <param name="id">Unique identifier of a peer.</param>
/// <param name="peer">
/// If found, the instance of <see cref="AirDropPeer"/> identified by <paramref name="id"/>,
/// <c>null</c> otherwise.
/// </param>
/// <returns>
/// <c>true</c> if the peer was found, <c>false</c> otherwise.
/// </returns>
public bool TryGetPeer(string id, out AirDropPeer peer)
```

These methods provide the surface area needed for the core pieces of AirDrop Anywhere to make a non-AirDrop compatible device discoverable. Let's break down how this interacts with other parts of the code.

### Registration

When registering a peer `AirDropService` creates an instance of `MulticastDnsService` using the `Id` property as the host & instance names. It keeps track of the peer in a dictionary keyed by `Id`. Then it tells `MulticastDnsServer` to announce DNS records for that service over the `awdl0` interface - this is exactly what happened in the previous version of the code except we're now dynamically announcing the existence of the peer rather than statically defining it. This announcement causes AirDrop to call the `/Discover` HTTP API using the hostname announced via mDNS - in our case the hostname is the same as the unique identifier of the peer.

`AirDropRouteHandler` has been modified to inject the instance of `AirDropPeer` associated with a request when it is instantiated - it does this by extracting the first part of the host header and performing a lookup using `TryGetPeer`. If that hostname resolves to an `AirDropPeer` then it continues executing the request as usual, otherwise it returns an HTTP 404. I say "as usual", but what does that mean for each API in AirDrop?

 - `/Discover` - we're currently operating in "Everybody" mode (instead of "Contacts-only" mode) so this API always returns details of the peer - notably its `Name` for rendering in the AirDrop UI.
 - `/Ask` - previously this always consented - now it makes a blocking call to `AirDropPeer.CanAcceptFilesAsync` to allow the peer to decide if the operation should continue or not
 - `/Upload` - previously this extracted uploaded files directly to the server's file system. Not very useful! Now it notifies the peer of each file that was extracted and exposes that file to the peer via HTTPS to allow it to download it.

### Unregistration

Unregistration effectively does the opposite of registration - it removes any trace of the peer in the server and announces it over mDNS with a TTL of 0 seconds. Announcing with a 0s TTL causes downstream mDNS caches to discard the records associated with the peer, making it disappear from any AirDrop browsers. Removing it from our server's state causes any requests to the HTTP API to return a 404 because `TryGetPeer` does not return the peer anymore.

## Peer Implementation

We've discussed how peering is intended to work, so let's dive into the implementation details for peering using SignalR. Out of the box SignalR provides connectivity via Websockets with fallback to server-sent events or long polling ([here](https://stackoverflow.com/a/12855533/871146) is a good post on the differences between the transports), but it abstracts it all away using the concept of a "hub". Clients connect to the hub and they can call methods on it or the server can call methods on the client - it's a two way connection. Additionally we can implement [streaming](https://docs.microsoft.com/en-us/aspnet/core/signalr/streaming?view=aspnetcore-5.0) from the server to the client or vice versa.

There are some restrictions on the server calling the client - notably that the client is unable to return a response to the server. However, by implementing bi-directional streaming, we can layer a fully async request/response mechanism on top of SignalR's streaming capablities. Consider a hub with the following [method](https://github.com/deanward81/AirDropAnywhere/blob/2021-06-14-cross-platform-bits/src/AirDropAnywhere.Cli/Hubs/AirDropHub.cs#L52):

```c#
/// <summary>
/// Starts a bi-directional stream between the server and the client.
/// </summary>
/// <param name="stream">
/// <see cref="IAsyncEnumerable{T}"/> of <see cref="AirDropHubMessage"/>-derived messages
/// from the client.
/// </param>
/// <param name="cancellationToken">
/// <see cref="CancellationToken"/> used to cancel the operation.
/// </param>
/// <returns>
/// <see cref="IAsyncEnumerable{T}"/> of <see cref="AirDropHubMessage"/>-derived messages
/// from the server.
/// </returns>
public async IAsyncEnumerable<AirDropHubMessage> StreamAsync(
    IAsyncEnumerable<AirDropHubMessage> stream, [EnumeratorCancellation] CancellationToken cancellationToken
);
```

This allows the server to send messages to the client (via the returned `IAsyncEnumerable<T>`) and for the client to send messages to the server via the `IAsyncEnumerable<T> stream` parameter. `AirDropHubMessage` generates a unique `Id` for each sent message, and a `ReplyTo` property containing the identifier of a message that the current message is in reply to. If `ReplyTo` is not set then the message is considered to be unsolicited.

When a client connects to the hub it calls `StreamAsync` and the server spins up a thread that is responsible for processing messages from the client's `IAsyncEnumerable`. It uses a [`Channel<T>`](https://devblogs.microsoft.com/dotnet/an-introduction-to-system-threading-channels/) as the "queue" of messages produced by the server - as messages are posted to the `Channel<T>` they are yielded to SignalR which sends them over the connection to the client.

### Callbacks

Under the hood the `T` used in our `Channel<T>` is actually a struct called `MessageWithCallback` that contains an `AirDropHubMessage` and an optional callback that is called if the message is in response to another message. Callbacks are handled by implementing `IValueTaskSource` - this is the `ValueTask` equivalent of using `TaskCompletionSource` and allows consumers of our SignalR implementation of `AirDropPeer` to `await` the result of an operation, even though that result is happening asynchronously on another thread (the one handling messages from the client). We keep track of the callback by storing a message's unique identifier and the `IValueTaskSource` representing the callback in a `Dictionary` associated with the connection. When a message is received from the client we check to see if the `ReplyTo` property value is in that `Dictionary` and, if so, we invoke the callback with the message from the client.

`IValueTaskSource` is relatively easy to implement, there's a struct in the runtime that implements the majority of its logic called `ManualResetValueTaskSourceCore<T>`, so our implementation, called [`CallbackValueTaskSource`](https://github.com/deanward81/AirDropAnywhere/blob/2021-06-14-cross-platform-bits/src/AirDropAnywhere.Cli/Hubs/AirDropHub.cs#L190), simply wraps it:

```c#
/// <summary>
/// Implementation of <see cref="IValueTaskSource{T}"/> that enables
/// a request/response-style conversation to occur over a SignalR full
/// duplex connection to a client. This is used to enable the hub to
/// perform a callback.
/// </summary>
private class CallbackValueTaskSource : IValueTaskSource<AirDropHubMessage>
{
    private ManualResetValueTaskSourceCore<AirDropHubMessage> _valueTaskSource;

    public void SetResult(AirDropHubMessage message) => _valueTaskSource.SetResult(message);
    
    public AirDropHubMessage GetResult(short token) => _valueTaskSource.GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => _valueTaskSource.GetStatus(token);

    public void OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags
    ) => _valueTaskSource.OnCompleted(continuation, state, token, flags);

    public void Reset() => _valueTaskSource.Reset();
}
```

When a reply message is received, and we've found a callback, we simply call `SetResult(AirDropHubMessage)` with the message. If a caller was `await`ing a `ValueTask` that wraps the `IValueTaskSource` it'll resume execution.

It's important to note the `Reset` method on this class - in order to minimise allocations we use an [`ObjectPool`(https://docs.microsoft.com/en-us/aspnet/core/performance/objectpool?view=aspnetcore-5.0) to keep a few instances of the class around for re-use. When we want an instance we call `ObjectPool.Get()` to get one, use it and then return it to the pool when we're done. `Reset` allows us to safely re-use that instance later.

That's a lot of words! Let's take a look at how that works when implemented in the SignalR peer (note: code has been delibrately simplified for the post):

```c#
// AirDropRouteHandler.AskAsync - simply calls `await` on the `CanAcceptFilesAsync` method
public async Task AskAsync()
{
    var askRequest = ...;
    var canAcceptFiles = await _peer.CanAcceptFilesAsync(askRequest);
    // do stuff with result
    // ...
}

// AirDropHubPeer.CanAcceptFilesAsync
public ValueTask<bool> CanAcceptFilesAsync(AskRequest askRequest)
{
    // transform the AskRequest into something understood by our SignalR client
    CanAcceptFilesRequestMessage request = ...;

    // get a callback instance from our pool
    var callback = _callbackPool.Get();
    try
    {
        // write the request and its callback to the server's IAsyncEnumerable
        await _serverQueue.WriteAsync(new MessageWithCallback(request, callback));
        // wait for the callback to be signalled, typically by a message being sent by the client
        // on another thread. This call will block until that happens.
        var result = await new ValueTask<AirDropHubMessage>(this, _valueTaskSource.Version);
        if (result is CanAcceptFilesResponseMessage typedResult)
        {
            return typedResult.Accepted;
        }

        // should never happen, likely a bug!
        throw new InvalidCastException(
            $"Cannot convert message of type {result.GetType()} to {typeof(CanAcceptFilesResponseMessage)}"
        );
    }
    finally
    {
        // we're done with the callback, return it to our pool
        _callbackPool.Return(callback);
    }
}
```

This might seem a little convoluted but it allows us to have a pretty useful request/response implementation over the top of a full duplex stream between our server and client. Importantly, the core pieces don't need to know _any_ details on how messages are relayed to the peer, they just `await` the call.

### Polymorphic Messages

By default SignalR uses `System.Text.Json` to serialize/deserialize messages across the wire. Usually that works just fine but here we're using bi-directional streaming with the abstract base class `AirDropHubMessage`. `System.Text.Json` has no idea how it should handle derivatives of this type so we need to give it a helping hand - enter [`PolymorphicJsonConverter`](https://github.com/deanward81/AirDropAnywhere/blob/2021-06-14-cross-platform-bits/src/AirDropAnywhere.Cli/Hubs/PolymorphicJsonConverter.cs#L15). This `JsonConverter` reads and writes JSON associated with the _concrete_ type of the object but wraps it in a named field so that it knows what runtime `Type` to deserialize to. We then add attributes to `AirDropHubMessage` that teach the converter which mappings it should handle:

```c#
[PolymorphicJsonInclude("connect", typeof(ConnectMessage))]
// ... more mappings here ...
public abstract class AirDropHubMessage
{
    public string Id { get; }
    public string? ReplyTo { get; }
}

public class ConnectMessage : AirDropHubMessage
{
    public string Name { get; }
}
```

When serialized the JSON looks like this:

```json
{
    "connect": {
        "Id": "471524b7b1914c3eb66bad8951277b74",
        "ReplyTo": null,
        "Name": "DWARD-MBP"
    }
}
```

When deserializing the `connect` key is used to lookup the right runtime type for the message - in this case `ConnectMessage` is used. This lets us maintain our simple streaming signature using `IAsyncEnumerable<AirDropHubMessage>` but allows us to pass any derived type to and from the connected parties.

## Uploading files

I mentioned above that our previous implementation of AirDrop's `/Upload` API just extracted files to the server's file system. Now that we can relay things to a peer we can forward arrays of bytes representing chunks of a file to them directly, yay! Unfortunately the story doesn't end there - if we connect to our SignalR server using a browser then our options for handling the array of bytes are somewhat limited - we need to construct a `Blob` and once we have all the chunks we can use some creative hacks to get the browser to trigger a "download" to the user's local machine. Except that we've already sent the bytes to the client so, in the case of large files, it's quite possible that the `Blob` is backed by memory or temporarily buffered somewhere else, likely with some kind of limits to prevent abuse.

Instead I've added a `StaticFileProvider` to the Kestrel instance spun up in the `AirDropAnywhere.Cli` server instance that maps to an `uploads` directory on the server. When we extract uploaded archives we generate a new directory here and new files are added to it. Once the archive is extracted we notify the client of each file that was extracted and its corresponding URL on the server - once the client has successfully downloaded all the files it needs then those files are removed from the server.

This allows us to stream the files directly to the client's browser without any unnecessary buffering along the way - it's a trade-off - we're expecting the server to have storage space to extract any archives sent its way but we can rely on the client's ability to download things directly rather than using semi-supported workarounds with `Blob` and `File` in the browser.

## Glueing it all together

Now we have a (relatively) sane approach to peering we can implement our first consumer. This first consumer will run at the command line and it'll perform the following steps:

1. Connect to an AirDrop Anywhere server using SignalR and immediately send a `ConnectMessage` containing the peer's name.
    1. Once connected the server will announce the peer over mDNS.
    1. AirDrop will discover the peer via this announcement and call the `/Discover` API over HTTPS.
1. Wait for a `CanAcceptFileRequestMessage` from the server. This is sent when a contact is tapped in the AirDrop UI, triggering a call to the `/Ask` API over HTTPS.
    1. Once received, the CLI displays a prompt to the user asking if they want to accept the files being sent.
    1. If they hit, _Yes_ then continue, otherwise go to 2. Either way, return the response as a `CanAcceptFileResponseMessage` so the server knows to how to continue.
1. For each file, receive a `FileUploadedRequestMessage` and use the URL within it to download the file from the server.
    1. Once the file is downloaded, send a `FileUploadedResponseMessage` to acknowledge receipt.

All of this logic is wrapped up in [`ClientCommand`](https://github.com/deanward81/AirDropAnywhere/blob/2021-06-14-cross-platform-bits/src/AirDropAnywhere.Cli/Commands/ClientCommand.cs) in the `AirDropAywhere.Cli` project. This makes use of [Spectre.Console](https://github.com/spectreconsole/spectre.console)'s wide array of formatting options to render the prompt and show output from the file download process.

There isn't anything particularly "magic" in this implementation - it is a simple SignalR client that renders some UI and uses an `HttpClient` to download files. The only quirks are ensuring that we ignore certificate validation errors when connecting over HTTPS for downloads or with SignalR - when running on a different machine the default ASP.NET development certificate is not trusted and therefore fails validation. Eventually I'll implement a certificate generator and change the validation to ensure the certificate is generated from a specific well-known root to eliminate the risk of MITM attacks.

We should be able to take the same structure used here and trivially apply it to a client implemented in JS or Blazor running entirely in the browser.

Here's a rough view of how it looks so far - this is the journey from iPhone to a Windows desktop via a Macbook Pro:

<table style="border:0">
  <tr>
    <td style="border:0">
      <img src="/img/airdrop-anywhere-10.jpg" width=160 alt="Sent from iPhone 1/2...">
      <img src="/img/airdrop-anywhere-11.jpg" width=160 alt="Sent from iPhone 2/2...">
    </td>
    <td style="border:0">
      <a href="/img/airdrop-anywhere-12.jpg" target="_blank">
        <img src="/img/airdrop-anywhere-12.jpg" width=320 alt="...via macOS...">
      </a>
    </td>
  </tr>
  <tr>
    <td style="border:0;text-align:center"><sub style="color:lightgray">Sent from iPhone...</sub></td>
    <td style="border:0;text-align:center"><sub style="color:lightgray">...via macOS...</sub></td>
  </tr>
  <tr>
    <td style="border:0">
      <a href="/img/airdrop-anywhere-13.png" target="_blank">
        <img src="/img/airdrop-anywhere-13.png" width=320 alt="...to Windows!">
      </a>
    </td>
  </tr>
  <tr>
    <td style="border:0;text-align:center"><sub style="color:lightgray">...to Windows!</sub></td>
  </tr>
</table>

## Next time

Next up, I'll be implementing the client to have all of this running entirely in the browser. I've put together a list of things I plan to work on over the coming weeks in a [GitHub project in the AirDropAnywhere repo](https://github.com/deanward81/AirDropAnywhere/projects/1).

I'll likely be moving the project to use .NET 6 in the very near future. In my last post I [detailed a workaround](/posts/2021-05-airdrop-anywhere-part-3#AwdlSocketTransportFactory) for allowing Kestrel to accept connections on the `awdl0` interface by adjusting the listen socket's options. Thanks to [Sourabh Shirhatti](https://github.com/shirhatti) at Microsoft an [issue](https://github.com/dotnet/aspnetcore/issues/32794) was created in the ASP.NET Core repo and I was able to [add support](https://github.com/dotnet/aspnetcore/pull/32827) for customising the creation of Kestrel's listen sockets directly to ASP.NET - this eliminates the need for my workaround altogether 🎉!

The end goal of all of this is to publish an executable that can be run on any device supporting AWDL or OWL (Apple or Linux with an RFMON-supporting wireless interface) as a daemon providing AirDrop services to a home network (I suspect this wouldn't scale to anything larger than that 🤔). Additionally I'd like to have `AirDropAnywhere.Core` published as a NuGet package that would allow AirDrop to be consumed by things like GUI applications, potentially allowing arbitrary files to be "dropped" to any application on any (supported) platform! Stay tuned!