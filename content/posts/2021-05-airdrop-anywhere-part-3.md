---
title: "AirDrop Anywhere - Part 3 - Receiving files"
date: 2021-05-17T13:00:00Z
tags: [.net, networking, airdrop, apple]
images: [img/airdrop-anywhere-cover.jpg]
summary: "Part three of the journey towards implementing AirDrop on any platform using .NET Core. This episode we cover receiving files from one Apple device to another using a .NET implementation of AirDrop."
---

> This is part 3 of a series of posts:
> - [Part 1 - Introduction](/posts/2021-05-airdrop-anywhere-part-1)
> - [Part 2 - Writing some code](/posts/2021-05-airdrop-anywhere-part-2)
> - [Part 3 - Receiving files](/posts/2021-05-airdrop-anywhere-part-3)
>     - [GitHub (File receiving bits)](https://github.com/deanward81/AirDropAnywhere/tree/2021-05-17-receiving-files)
> - [GitHub (latest)](https://github.com/deanward81/AirDropAnywhere/tree/main) - **NOTE** still work in progress!

In the [previous episode](/posts/2021-05-airdrop-anywhere-part-2) we talked about the challenges I came across while implementing the mDNS advertisements necessary to support AirDrop. By the end of this episode we should be able to send a file from an Apple device to another Apple device running our service 🎉.

## How it works

From [last time](/posts/2021-05-airdrop-anywhere-part-2#Interactions) - AirDrop works by advertising the endpoint associated with an HTTPS listener. Right now we don't have _anything_ listening for requests - so we'll need something listening on HTTPS and have it implement the relevant API routes that AirDrop expects. To do so we need to know what the API surface looks like!

Typically to reverse engineer an API implemented over HTTPS we'd use something like [Fiddler](https://www.telerik.com/fiddler) to intercept HTTPS traffic and decrypt it, but because AirDrop configures an ad-hoc connection over the AWDL interface, we don't have anyway to force it to route traffic via Fiddler. Our next option is to use [Wireshark](https://www.wireshark.org/) on the AWDL interface and configure it to decrypt TLS. Fortunately, AirDrop supports self-signed certificates for whatever is listening on the HTTPS endpoint - so we can trivially use the ASP.NET Core Development Certificate stored in the keychain and configure Wireshark to use it. Then all we need to do is sniff packets on the listening device's AWDL interface...

However, other than for monitoring what's going on, we don't need to do this to work out the API surface! [PrivateDrop](https://github.com/seemoo-lab/privatedrop) and [OpenDrop](https://github.com/seemoo-lab/opendrop) both provide an implementation of the API that we can translate to the equivalent C#.

### API Endpoints

AirDrop needs 3 endpoints in order to function:

1. `/Discover` - accepts a POST containing information about the sender of the request and expects a return message containing details of the receiver such as its name and capabilities. This route is called immediately after an mDNS request returns the details of our HTTPS endpoint.
1. `/Ask` - accepts a POST containing information about the sender and metadata on files it wants to send. This route is called when the user clicks a recipient in the AirDrop UI and is expected to block until the receiver has confirmed that they want to receive the files.
1. `/Upload` - called once the sender has been authorised to send files. It performs an HTTP POST containing the files' data. What format those files are in depends on capabilities returned by the call to `/Discover` and the flags contained in the TXT record advertised using mDNS.

## Spinning up the server

Exposing the HTTPS endpoint we need is as simple as spinning up an instance of Kestrel and having it listen using the default development certificate. Using that certificate is a little hacky but it works and we can generate our own self-signed certificate later.

This code is heavily cutdown for the blog, but it effectively looks like this in the `AirDropAnywhere.Cli` project:

```c#
WebHost.CreateDefaultBuilder()
    .ConfigureAirDrop(
        options =>
        {
            options.ListenPort = 34553;
        }
    )
    .Build()
    .Run();
```

`ConfigureAirDrop` is an extension method on `IWebHostBuilder` in the `AirDropAnywhere.Core` project - eventually we'll allow any application to expose AirDrop by consuming the `Core` project as a NuGet package which is why it's structured like this.

```c#
public static IWebHostBuilder ConfigureAirDrop(this IWebHostBuilder builder, Action<AirDropOptions>? setupAction = null) => 
    builder
        .ConfigureKestrel(
            options =>
            {
                var airDropOptions = options.ApplicationServices.GetRequiredService<IOptions<AirDropOptions>>();
                
                options.ConfigureEndpointDefaults(
                    options =>
                    {
                        // TODO: use our own self-signed certificate!
                        options.UseHttps();
                    }
                );
                        
                options.ListenAnyIP(airDropOptions.Value.ListenPort);
            }
        )
        .ConfigureServices(
            services =>
            {
                services.AddRouting();
                services.AddAirDrop(setupAction);
            }
        )
        .Configure(
            app =>
            {
                app.UseRouting();
                app.UseEndpoints(
                    endpoints =>
                    {
                        endpoints.MapAirDrop();
                    }
                );
            }
        );
```

This is pretty simple - it configures Kestrel to use HTTPS, for it to listen on any IP address and use endpoint routing. We expose some options that allow us to configure things like the listening port and then call down into extension methods that add relevant services and map the API's endpoints:

```c#
public static IServiceCollection AddAirDrop(this IServiceCollection services, Action<AirDropOptions>? setupAction = null)
{
    services.AddScoped<AirDropRouteHandler>();
    services.AddSingleton<AirDropService>();
    services.AddSingleton<IHostedService>(s => s.GetService<AirDropService>()!);
    services.AddOptions<AirDropOptions>().ValidateDataAnnotations();

    if (setupAction != null)
    {
        services.Configure(setupAction);
    }

    return services;
}

public static IEndpointRouteBuilder MapAirDropCore(IEndpointRouteBuilder endpoints)
{  
    endpoints.MapPost("Discover", ctx => AirDropRouteHandler.ExecuteAsync(ctx, (c, r) => r.DiscoverAsync(c)));
    endpoints.MapPost("Ask", ctx => AirDropRouteHandler.ExecuteAsync(ctx, (c, r) => r.AskAsync(c)));
    endpoints.MapPost("Upload", ctx => AirDropRouteHandler.ExecuteAsync(ctx, (c, r) => r.UploadAsync(c)));
    return endpoints;
}
```

You'll see that we add an `IHostedService` called `AirDropService` - this is what handles our mDNS advertisements and sets up AWDL on macOS. [`IHostedService`](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-5.0&tabs=visual-studio) exposes a `StartAsync` and `StopAsync` that allows us to tie the lifetime of our service to the underlying `WebHost` which makes it perfect for long-running services.

Then, we add some POST endpoints to endpoint routing and have them hook into `AirDropRouteHandler`. `ExecuteAsync` simply does some boilerplate bits per request - it spins up an instance of `AirDropRouteHandler` and then calls an instance method to do the work of handling the request.

It's possible that just moving this to WebApi or MVC would be a better long term bet, something to consider for a future refactor.

### But does it work...

After spinning up the application, sniffing the AWDL interface using Wireshark and gleefully opening AirDrop on my iPhone it... _doesn't_ work. Well, pants 🤦‍♂️. Wireshark indicated that opening the connection to the HTTPS endpoint fails with a timeout. I gave this a little thought and quickly realised that it was for the same reason that my mDNS implementation failed the first time around - I need to explicitly tell the OS that this socket needs to use the AWDL interface. Easy peasy!

Well, I thought that would be the case - surely Kestrel has trivial extensibility points to mess with its underlying sockets? Turns out that isn't the case at all. Kestrel uses an implementation of the `IConnectionListenerFactory` interface called `SocketTransportFactory`. This is responsible for binding to the underlying transport and returning an `IConnectionListener`. I don't particularly want to implement all the plumbing that `SocketTransportFactory` does and, unfortunately, all of its internals are, ummmm, `internal`.

Reflection to the rescue! Yes, this is terrible practice but it gets me where I need to be so I'll take it. Here's what I came up (again, abbreviated for the blog post - you can see the actual implementation [here](https://github.com/deanward81/AirDropAnywhere/blob/2021-05-17-receiving-files/src/AirDropAnywhere.Core/HttpTransport/AwdlSocketTransportFactory.cs));

```c#
// Actual implementation: https://github.com/deanward81/AirDropAnywhere/blob/2021-05-17-receiving-files/src/AirDropAnywhere.Core/HttpTransport/AwdlSocketTransportFactory.cs
internal class AwdlSocketTransportFactory : IConnectionListenerFactory
{
    private readonly IConnectionListenerFactory _connectionListenerFactory;
    private readonly FieldInfo _listenSocketField;
    
    public AwdlSocketTransportFactory(IOptions<SocketTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _connectionListenerFactory = new SocketTransportFactory(options, loggerFactory);
        // HACK: this merry little reflective dance is because of sealed internal classes
        // and no extensibility points, yay :/
        _listenSocketField = typeof(SocketTransportFactory).Assembly
            .GetType("Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketConnectionListener")
            .GetField("_listenSocket", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var transport = await _connectionListenerFactory.BindAsync(endpoint, cancellationToken);
        // HACK: fix up the listen socket to support listening on AWDL
        var listenSocket = (Socket?) _listenSocketField.GetValue(transport);
        if (listenSocket != null)
        {
            listenSocket.SetAwdlSocketOption();
        }
        return transport;
    }
}
```

We can tell Kestrel to use this by updating our `AddAirDrop` extension method to use the service, but only on macOS:

```c#
if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    services.AddSingleton<IConnectionListenerFactory, AwdlSocketTransportFactory>();
}
```

And... it works! AirDrop successfully POSTs a message to `/Discover` and we promptly respond with a `200 OK`. Naturally this doesn't make the sender _do_ anything, but it's a start!

## `/Discover`

### Reading the request

Upon inspection of the POST body sent to `/Discover` we see something like this:

```
bplist00�_SenderRecordDataO�0�        *�H��
��$��<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
        <key>Version</key>
        <integer>2</integer>
        <key>altDsID</key>
        <string>000971-05-05fd81cb-382d-4546-97f7-4a04d83d8396</string>
        <key>encDsID</key>
        <string>000971-05-05fd81cb-382d-4546-97f7-4a04d83d8396</string>
        <key>SuggestValidDuration</key>
        <integer>2592000</integer>
        <key>ValidAsOf</key>
        <date>2021-05-17T09:57:27Z</date>
        <key>ValidatedEmailHashes</key>
        <array>
                <string>{hash}</string>
        </array>
        <key>ValidatedPhoneHashes</key>
        <array>
                <string>{hash}</string>
        </array>
</dict>
</plist>*�H����s��e     �0
... lots of binary
```

What the heck is this? `Content-Type` is set to `application/octet-stream` and, based upon the first few bytes of the payload, it turns out to be a [binary-encoded plist](https://medium.com/@karaiskc/understanding-apples-binary-property-list-format-281e6da00dbd). Plist is a format commonly used in the Apple ecosystem but, seriously, wtf - why on earth anybody would use this in an HTTP API is beyond me - it's wildly verbose, even in its "binary" representation - look at all the frickin' XML!

Fortunately a few people have gone to the bother of implementing plist for .NET - I picked [plist-cli](https://www.nuget.org/packages/plist-cil/) because it seems to represent things in a coherent way. That said it materializes things as a set of `NS*` instances rather than, say, a POCO (plain ol' CLR object). Ideally I want to materialize this payload as a POCO for easier consumption downstream. I ended up writing a few wrappers that take the raw request body, deserialize it using `plist-cli` and then uses some more reflection nastiness to bind it to a POCO. Similarly, for responses it does everything in reverse - translates the POCO to something `plist-cli` understands and then lets it take over serialization to the wire format. My intent is to replace this with something that reads/writes plist directly from the POCO, probably using some [source generator](https://devblogs.microsoft.com/dotnet/introducing-c-source-generators/) fun but, for now, what I have works. See the [Serialization](https://github.com/deanward81/AirDropAnywhere/tree/2021-05-17-receiving-files/src/AirDropAnywhere.Core/Serialization) namespace for the code that handles all this.

A couple of extension methods add support for reading / writing the plist format from the HTTP request / response:

```c#
/// <summary>
/// Writes an object of the specified type from the HTTP request using Apple's plist binary format.
/// </summary>
public static ValueTask<T> ReadFromPropertyListAsync<T>(this HttpRequest request) where T : class, new()
{
    if (!request.ContentLength.HasValue || request.ContentLength > PropertyListSerializer.MaxPropertyListLength)
    {
        throw new HttpRequestException("Content length is too long.");
    }

    return PropertyListSerializer.DeserializeAsync<T>(request.Body);
}

/// <summary>
/// Writes the specified object to the HTTP response in Apple's plist binary format.
/// </summary>
public static ValueTask WriteAsPropertyListAsync<T>(this HttpResponse response, T obj) where T : class
{
    if (obj == null)
    {
        throw new ArgumentNullException(nameof(obj));
    }

    response.ContentType = "application/octet-stream";
    return PropertyListSerializer.SerializeAsync(obj, response.Body);
}
```

Now we can _read_ the thing that AirDrop sent us, let's see what we can do with it!

### Understanding the request

This payload represents a set of information that the sender knows about - notably the hashes of the sender's email address and phone number. This is used by AirDrop in "contact-only" receive mode to see if it knows the sender and allows the receiver to determine whether it should "expose" its details to them.

Contact information is embedded within the plist we received above as an `NSData` instance - effectively a binary blob. It turns out it's a [PKCS7](https://en.wikipedia.org/wiki/PKCS_7)-signed payload, signed with Apple's root CA. .NET Core gives us the `SignedCms` class to validate the signature. To use it we need a copy of Apple's root CA public key which we can extract from KeyChain or, easier, just grab directly from [PrivateDrop](https://github.com/seemoo-lab/PrivateDrop-Base/tree/be54e158b3a8e3d86adfb804b4f54b308733bef8/Sources/PrivateDrop%20Base/Resources/Certificates) and embed it as a resource in `AirDropAnywhere.Core`. Here's the code to validate the payload (taken from [`DiscoverRequest.cs`](https://github.com/deanward81/AirDropAnywhere/blob/2021-05-17-receiving-files/src/AirDropAnywhere.Core/Models/DiscoverRequest.cs)):

```c#
/// <summary>
/// Body of a request to the /Discover endpoint in the AirDrop HTTP API.
/// </summary>
public class DiscoverRequest
{
    /// <summary>
    /// Gets a binary blob representing a PKCS7 signed plist containing
    /// sender email and phone hashes. This is validated and deserialized into a <see cref="RecordData"/>
    /// object by <see cref="TryGetSenderRecordData"/>.
    /// </summary>
    public byte[] SenderRecordData { get; private set; } = Array.Empty<byte>();

    public bool TryGetSenderRecordData(out RecordData? recordData)
    {
        if (SenderRecordData == null || SenderRecordData.Length == 0)
        {
            recordData = default;
            return false;
        }
        
        // validate that the signature is valid
        var signedCms = new SignedCms();
        try
        {
            signedCms.Decode(SenderRecordData);
            signedCms.CheckSignature(
                // load the apple certificate from embedded resources
                new X509Certificate2Collection(ResourceLoader.AppleRootCA), true
            );
        }
        catch
        {
            recordData = default;
            return false;
        }

        recordData = PropertyListSerializer.Deserialize<RecordData>(signedCms.ContentInfo.Content);
        return true;
    }
}
```

This ends up giving us a POCO containing the sender email and phone hashes that we can use for validation:

```c#
public class RecordData
{
    public IEnumerable<string> ValidatedEmailHashes { get; private set; } = Enumerable.Empty<string>();
    public IEnumerable<string> ValidatedPhoneHashes { get; private set; } = Enumerable.Empty<string>();
}
```

Right now, I've decided not to implement "contacts-only" mode so we don't need this - yet! I _will_ implement that part later - for the moment I'm more interested in actually receiving a file.

With that in mind, we'll validate that the data is legit (by calling `TryGetSenderRecordData`) but we'll simply return the `/Discover` response - a payload containing _our_ details - the name of the receiver and its capabilities:

```c#
public class DiscoverResponse
{
    /// <summary>
    /// Gets the receiver computer's name. Displayed when selecting a "contact" to send to.
    /// </summary>
    public string ReceiverComputerName { get; }
    /// <summary>
    /// Gets the model name of the receiver.
    /// </summary>
    public string ReceiverModelName { get; }
    /// <summary>
    /// Gets the UTF-8 encoded bytes of a JSON payload detailing the
    /// media capabilities of the receiver.
    /// </summary>
    public byte[] ReceiverMediaCapabilities { get; }
}
```

### Capabilities

A receiver can indicate to the sender what types of media it is capable of receiving in the response body. Typically this lights up more advanced formats such as the [High Efficiency Image Format](https://en.wikipedia.org/wiki/High_Efficiency_Image_File_Format), but I don't intend to support these. I have no idea if the underlying devices I want to use AirDrop with can support receiving these kinds of files so we return simply indicate that we do not support any capabilties - effectively dumbing down the media to its most compatible format.

This is expected to be provided as the UTF-8 encoded bytes of a JSON payload inside of our binary-encoded plist. Honestly, can this get any worse?

### And the result...

After implementing this endpoint we can see that AirDrop now "sees" our service, GUID and all. Hooray!

<img src="/img/airdrop-anywhere-6.jpg" width=160 alt="Service in AirDrop"><br/>
<sub style="color:lightgray">It's Alive!</sub>

Next, we need to see if the receiver wants to receive the files we're sending...

## `/Ask`

This is a relatively simple API - it's called when a user taps our icon in the UI and is intended to block until the receiver decides whether or not they want to receive what we're sending to them.

Again, it's a binary-encoded plist that I've mapped to the following POCO:

```c#
public class AskRequest
{
    /// <summary>
    /// Gets the sender computer's name. Displayed when asking for receiving a file not from a contact
    /// </summary>
    public string SenderComputerName { get; private set; }
    /// <summary>
    /// Gets the model name of the sender
    /// </summary>
    public string SenderModelName { get; private set; }
    /// <summary>
    /// Gets the service id distributed over mDNS
    /// </summary>
    public string SenderID { get; private set; }
    /// <summary>
    /// Gets the bundle id of the sending application
    /// </summary>
    public string BundleID { get; private set; }
    /// <summary>
    /// Gets a value indicating whether the sender wants that media formats are converted
    /// </summary>
    public bool ConvertMediaFormats { get; private set; }
    /// <summary>
    /// Gets the sender's contact information.
    /// </summary>
    public byte[] SenderRecordData { get; private set; }
    /// <summary>
    /// Gets a JPEG2000 encoded file icon used for display.
    /// </summary>
    public byte[] FileIcon { get; private set; }
    /// <summary>
    /// Gets an <see cref="IEnumerable{T}"/> of <see cref="FileMetadata"/> objects
    /// containing metadata about the files the sender wishes to send.
    /// </summary>
    public IEnumerable<FileMetadata> Files { get; private set; } = Enumerable.Empty<FileMetadata>();
}

public class FileMetadata
{
    public string FileName { get; private set; }
    public string FileType { get; private set; }
    public string FileBomPath { get; private set; }
    public bool FileIsDirectory { get; private set; }
    public bool ConvertMediaFormats { get; private set; }
}
```

There's a fair bit in the request, but the tl;dr is that it contains some metadata about the sender _and_ about the files that they are sending to us. As we start to implement the clients that do not natively support AirDrop we'll need to implement this endpoint properly but we can get away with returning an HTTP OK with the following response in binary-encoded plist format:

```c#
public class AskResponse
{   
    /// <summary>
    /// Gets the receiver computer's name.
    /// </summary>
    public string ReceiverComputerName { get; }
    /// <summary>
    /// Gets the model name of the receiver.
    /// </summary>
    public string ReceiverModelName { get; }
}
```

### `/Upload`

This is the endpoint that AirDrop calls to actually transfer files from sender to receiver. As soon as we confirm the `/Ask` request AirDrop immediately calls this endpoint with a POST body containing the files we asked for. However, as with the rest of this API there's some quirks under the hood.

When we advertise our instance of AirDrop using mDNS we provide a TXT record called `Flags` that contains a bitmask of the following flags enum:

```c#
[Flags]
internal enum AirDropReceiverFlags : ushort
{
    Url = 1 << 0,
    DvZip = 1 << 1,
    Pipelining = 1 << 2,
    MixedTypes = 1 << 3,
    Unknown1 = 1 << 4,
    Unknown2 = 1 << 5,
    Iris = 1 << 6,
    Discover = 1 << 7,
    Unknown3 = 1 << 8,
    AssetBundle = 1 << 9,
}
```

These were all derived from the work performed by [Secure Mobile Networking Lab](https://github.com/seemoo-lab) in their [OpenDrop](https://github.com/seemoo-lab/OpenDrop) and [PrivateDrop](https://github.com/seemoo-lab/PrivateDrop) implementations. By default macOS broadcasts a bitmask value of 1019 which corresponds to `Url | DvZip | MixedTypes | Unknown1 | Unknown2 | Iris | Discover | Unknown3 | AssetBundle`. This value is interpreted by the sender of files and is used to ascertain capabilities of the receiver. Sounds a bit like the media capabilities thing doesn't it? I'm unsure why these are split, but I suspect that it's possible that `/Discover` came _after_ this implementation (I wild guess based upon the presence of the `Discover` bit in the enum above).

One of these flags is the `DvZip` bit - this indicates what format the POST body to `/Upload` is in. When this bit is present we get an opaque binary format that the `file` command cannot understand and none of the tooling on my Mac seems to understand eiter. It appears to be totally undocumented :(. When it's not there then we get a different file format - judging from OpenDrop it appears we're dealing with an OIDC encoded [`cpio` archive](https://manpages.ubuntu.com/manpages/bionic/man5/cpio.5.html). This format consists of a stream of "records" each containing a header with metadata about the data following it:

```c
struct cpio_odc_header {
    char    c_magic[6];
    char    c_dev[6];
    char    c_ino[6];
    char    c_mode[6];
    char    c_uid[6];
    char    c_gid[6];
    char    c_nlink[6];
    char    c_rdev[6];
    char    c_mtime[11];
    char    c_namesize[6];
    char    c_filesize[11];
};
```

OIDC is an ASCII format and each of the fields in the struct are represented as an octal string. `namesize` and `filesize` indicate the size of the filename and data following the record. The stream of records ends with a record that points at the filename `TRAILER!!!`. Once we hit this we know we've successfully parsed the contents of the archive.

To test this I saved an uploaded archive directly to disk and poked at it with `cpio` but I couldn't get it to extract. Finally, after banging my head against the wall for a while I ended up running `file` which prompt told me that the archive was also GZIP compressed. I completely missed this because AirDrop doesn't bother to set the `Content-Encoding` header on the request, ya know like the spec says 😡. After running `gunzip` the `cpio` command works perfectly and I can extract the files sent over AirDrop successfully!

Now to implement this in C# - there's a [few implementations](https://www.nuget.org/packages?q=cpio) used for extracting CPIO archives on NuGet, but none of them really play well with the async-only bits of the HTTP pipeline in .NET Core. I really want to extract directly from the stream (something that `cpio`'s format is great for) and many of the NuGet packages expect to operate on the file system. Instead I wrote a streaming extraction that understands (only) OIDC format archives and interfaces with the `PipeReader` from an `HttpRequest`. When coupled with a `GZipStream` to decompress as we go the endpoint can now successfully extract uploaded files to a temporary directory. `CpioArchiveReader` is a little lengthy to breakdown here so you can see the code [here](https://github.com/deanward81/AirDropAnywhere/blob/2021-05-17-receiving-files/src/AirDropAnywhere.Core/Compression/CpioArchiveReader.cs).

The most difficult thing about that implementation is grokking how `PipeReader` works but Marc Gravell has a [good set of posts](https://blog.marcgravell.com/2018/07/pipe-dreams-part-1.html) that I used as a primer and he also graciously spent some time to help me understand what the hell I was doing there. Thanks Marc!

### But, does it work?

Yes, it does! I sent a few files (both individually and as a "bundle") and they get successfully decompressed and extracted to the file system 🥳. While this is next to useless for actual consumption (files are written to a temporary directory in this version), this is an important first step to hooking up non-native AirDrop clients to the proxy.

<table style="border:0">
  <tr>
    <td style="border:0">
      <img src="/img/airdrop-anywhere-7.jpg" width=160 alt="Sent from iPhone...">
    </td>
    <td style="border:0">
      <img src="/img/airdrop-anywhere-8.png" width=320 alt="...to macOS">
    </td>
  </tr>
  <tr>
    <td style="border:0;text-align:center"><sub style="color:lightgray">Sent from iPhone...</sub></td>
    <td style="border:0;text-align:center"><sub style="color:lightgray">...to macOS</sub></td>
  </tr>
</table>

Note that the current implementation is a security nightmare - it effectively allows any AirDrop device to blindly send files and have them stored on the target device. We'll need to address these shortcomings before running this proxy on anything that isn't just used for development!

## What's next?

Now we have the ability to receive files from an Apple device our next task is to define the interface that allows non-native clients to be discovered via our proxy. Next time we'll dig into what that interface looks like and how we'll hook it up so we can use it via the CLI and an internally hosted site. Enjoy!