---
title: "AirDrop Anywhere - Part 2 - Writing some code"
date: 2021-05-04T15:00:00Z
tags: [.net, networking, airdrop, apple]
images: [img/iconfiguration-cover.png]
---

> This is part 2 of a series of posts:
> - [Part 1 - Introduction](/posts/2021-05-airdrop-anywhere-part-1)
> - [Part 2 - Writing some code](/posts/2021-05-airdrop-anywhere-part-2)
> - [GitHub](https://github.com/deanward81/AirDropAnywhere) - **NOTE** still work in progress!


[Last time](2021-04-airdrop-anywhere-part-1) we broke down the problem of implementing AirDrop so that we can support sending and receiving files from devices that do not natively support AirDrop. After some to and fro we landed on an implementation that involves creating a "proxy" running on an Apple device or Linux with [OWL](https://owlink.org).

## Pre-requisites

### Project structure
I've decided to structure all of this as a "Core" project that'll contain the bulk of the AirDrop implementation. This will be able to be plugged into any .NET Core application in order to provide AirDrop services and each application can provide an implementation of an interface in order to handle file sending and receiving. Our project hierarchy will look like this:

```
|---AirDropAnywhere.Core
    |
    |--AirDropAnywhere.Cli
    |
    |--AirdropAnywhere.Web
```

`AirDropAnywhere.Core` will contain all our shared services (e.g. mDNS advertisements, core AirDrop HTTP API) and the means to configure them in a `WebHost`.

`AirDropAnywhere.Cli` will be a CLI application hosting the services and rendered using [Spectre.Console](https://github.com/spectreconsole/spectre.console). It'll typically be used as a way to send / receive for the current machine via the command line.

Individual devices will connect to an application hosted by `AirDropAnywhere.Web`. I've gone back and forth on this a little - I thought about implementing the bits that allow a device to send / receive content via the "proxy" as a GRPC-streaming service and implementing a UI using [MAUI](https://github.com/dotnet/maui). This satisfies the need of being cross-platform, but requires each device to have a copy of the application installed locally. I _could_ use [gRPC-Web](https://grpc.io/docs/platforms/web/basics/) and hook everything into Blazor or some client-side JS but it feels like a fair bit of boilerplate shennanigans that is elegantly solved by SignalR.

So, next, I considered whether to use a Blazor client-side application using SignalR to connect to the "backend". But, I can't see any great incentive to use Blazor for this kind of application - it doesn't have a huge amount of interactivity and there's a great implementation of SignalR that can be consumed from Javascript ¯\_(ツ)_/¯. In the end I decided that I'd implement this as a bog-standard SPA using [Vue.js](https://vuejs.org/) and connecting to the backend using a SignalR-based "API". I've never used Vue.js but have heard good things - so let's give it a try!

### Tools
To create the project I'm using a combination of [JetBrains Rider](https://www.jetbrains.com/rider/) and [VS Code](https://code.visualstudio.com/) all running on an ancient 2013 Macbook Pro. I'm using Rider because I've become fond of the speed and refactoring wizardry of the JetBrains toolset and VS Code so that I can remote debug on a Raspberry Pi over SSH (more on that later).

To allow us to inspect the mDNS / DNS-SD services we're publishing I've made use of an app called [Discovery DNS-SD Browser](https://apps.apple.com/us/app/discovery-dns-sd-browser/id1381004916?mt=12). This was previously called Bonjour Browser. It gives us a tree view of the mDNS services available to us. Additionally the `dns-sd` utility is very useful.

It's also incredibly useful to be able to inspect network traffic with some of the things we're using here (particularly mDNS) so I've made copious use of [Wireshark](https://www.wireshark.org/) to make sure the application does the right thing over the wire by monitoring the `awdl0` interface on the Mac and using remote SSH packet capture for doing the same on the Raspberry Pi.

## Building the system

To help visualize how I anticipate this working I drew a simple diagram of the system and how it interacts with the various devices. As we work through implementation I'll refer back to this diagram and refine parts of it as the problems we face become clear!

<img src="/img/airdrop-anywhere-2.png" width=480 alt="AirDrop Anywhere Architecture"><br/>
<sub style="color:lightgray">AirDrop Anywhere Architecture</sub>

We'll start by implementing the two components that allow us to communicate with devices using AirDrop - mDNS and the AirDrop HTTP API. To do so we'll spin up an .NET Core `WebHost` and configure the endpoints needed for the HTTP API and an `IHostedService` that will manage the lifetime of our mDNS service. Off we go!

### mDNS
To allow AirDrop-compatible devices to find our implementation of AirDrop we need to advertise our service and its related SRV and TXT records over mDNS. Richard Schneider's [mDNS implementation](https://github.com/richardschneider/net-mdns) looks like it would fit the bill here, but, after implementing the relevant pieces in `AirDropAnywhere.Core`, I found that Wireshark was not picking up mDNS responses from the service to queries sent from my iOS-based devices over the `awdl0` interface.

Further investigation uncovered [this post](https://yggdrasil-network.github.io/2019/08/19/awdl.html) from a project called [yggdrasil](https://github.com/yggdrasil-network/yggdrasil-go) that creates a mesh of devices to provide an end-to-end encrypted IPv6 network. That post details how they enabled meshing across the AWDL interface in Apple devices and there are two things that need to happen for a socket to be able to communicate over the `awdl0` interface:

1. OS needs to "wake-up" AWDL. Typically this is handled by the [`NSNetServiceBrowser`](https://developer.apple.com/documentation/foundation/netservicebrowser) class when the `includesPeerToPeer` property is enabled. Unfortunately it's [a pain to call](https://stackoverflow.com/questions/44665544/how-to-pinvoke-appkit-methods-from-net-core-application) this from C# - - `Xamarin.MacOS` provides a C# implementation but that's currently tied to Mono (.NET 6 will likely address this). `yggdrasil` uses a [small snippet of ObjC](https://github.com/yggdrasil-network/yggdrasil-go/blob/master/src/multicast/multicast_darwin.go#L5-L23) and uses it to wake up the `awdl0` interface:

 ```objc
#import <Foundation/Foundation.h>
NSNetServiceBrowser *serviceBrowser;
void StartAWDLBrowsing() {
	if (serviceBrowser == nil) {
		serviceBrowser = [[NSNetServiceBrowser alloc] init];
		serviceBrowser.includesPeerToPeer = YES;
	}
	[serviceBrowser searchForServicesOfType:@"_yggdrasil._tcp" inDomain:@""];
}
void StopAWDLBrowsing() {
	if (serviceBrowser == nil) {
		return;
	}
	[serviceBrowser stop];
}
 ```

 I've taken a similar approach - [`libnative.m`](https://github.com/deanward81/tree/main/AirDropAnywhere/src/AirDropAnywhere.Core/libnative.m) contains code similar to above. I've added a `BeforeBuild` target in `AirDropAnywhere.Core.csproj` that runs `clang` to compile `libnative.m` to `libnative.so` and then used P/Invoke to execute the `StartAWDLBrowsing` and `StopAWDLBrowsing` functions. This instructs the OS to wake-up AWDL so we can receive traffic on the `awdl0` interface.

2. Next we need to allow our sockets to talk using AWDL - in MacOS we need to configure some socket options that notify the OS that we want to do be able to use `awdl0`. That means calling the following on whatever sockets need to do so:

```c#
private static readonly ReadOnlyMemory<byte> _trueSocketValue = BitConverter.GetBytes(1);

public static void SetAwdlSocketOption(this Socket socket)
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        return;
    }

    // from Apple header files:
    // sys/socket.h: #define	SO_RECV_ANYIF	0x1104
    const int SO_RECV_ANYIF = 0x1104;
    socket.SetRawSocketOption(
        (int)SocketOptionLevel.Socket, SO_RECV_ANYIF, _trueSocketValue.Span
    );
}
```

This works great if I control the socket, but `net-mdns` handles all of that for us. In an attempt to get this to work I hacked some reflection together to get at the `Socket` instances used by `net-mdns` and call `SetAwdlSocketOption` on them. It compiled, it ran, it didn't throw exceptions, but still _nothing_ from our service in Wireshark - I see traffic coming _from_ my iPhone over `awdl0` - so our ObjC code executes and wakes up the interface, but `net-mdns` does not respond to the packets. _Sigh_.

After debugging through the `net-mdns` code base I found a couple of empty `catch` blocks that were hiding some socket-binding issues. Binding UDP-based sockets to `awdl0` is a little painful and some of the binds and socket options used by `net-mdns` were causing it to throw and not actually use the `awdl0` interface at all! I've gone back and forth whether it's worth fixing the issues in `net-mdns` or whether it's simpler to take some of its approaches and implement something a little more specific for this scenario and landed on implementing the code I need directly in `AirDropAnywhere.Core`. There are some things that just don't make sense in a general mDNS implementation.

As a result I've taken key parts of `net-mdns`, re-used the excellent [net-dns](https://github.com/richardschneider/net-dns) library for handling the various DNS record types that are needed for mDNS and implemented a `MulticastDnsServer` that addresses the specific needs of binding to the `awdl0` interface:

 - removal of service discovery and querying. We only need to advertise `_airdrop._tcp` with the relevant SRV and TXT records so this implementation focuses on the advertisement bits of mDNS
 - it handles setting the right socket options for AWDL
 - mDNS multicast responses are sent over the same interface that the request was received on - something which AWDL is particularly sensitive to.
 - it's async throughout - `net-mdns` had a number of sync-over-async patterns in the code that could cause deadlocks.

And the result is.... _it works_! 

<img src="/img/airdrop-anywhere-3.png" width=480 alt="Wireshark mDNS Traffic"><br/>
<sub style="color:lightgray">Wireshark mDNS Traffic</sub>

Here we can see traffic to the IPv6 multicast `ff02:fb` address from my iPhone on the `awdl0` interface. The row underlined in red is our call to `StartAWDLBrowsing` - this is what initiates AWDL on my Macbook. Next, the rows underlined in orange are an mDNS query for `_airdrop._tcp` from my iPhone, followed by an mDNS response from my Macbook answering the query.

And to confirm that all the relevant DNS records are working using `dns-sd`:

```
# browse for AirDrop using the awdl0 interface
deanw@dward-mbp$ dns-sd -i awdl0 -B _airdrop._tcp local
Using interface 6
Browsing for _airdrop._tcp.local
DATE: ---Fri 30 Apr 2021---
15:02:37.919  ...STARTING...
Timestamp     A/R    Flags  if Domain               Service Type         Instance Name
15:02:37.919  Add        2   6 local.               _airdrop._tcp.       z9hxvxvqetnh
^C

# resolve the hostname of the AirDrop service z9hxvxvqetnh
deanw@dward-mbp$ dns-sd -i awdl0 -L z9hxvxvqetnh _airdrop._tcp local
Using interface 6
Lookup z9hxvxvqetnh._airdrop._tcp.local
DATE: ---Fri 30 Apr 2021---
15:04:51.596  ...STARTING...
15:04:51.597  z9hxvxvqetnh._airdrop._tcp.local. can be reached at faec1782-f0b7-40a3-bbe2-5f7dae0164eb.local.:34553 (interface 6)
 flags=653
^C

# resolve the IP address of faec1782-f0b7-40a3-bbe2-5f7dae0164eb.local
deanw@dward-mbp$ dns-sd -i awdl0 -G v6 faec1782-f0b7-40a3-bbe2-5f7dae0164eb.local.
Using interface 6
DATE: ---Fri 30 Apr 2021---
15:06:24.219  ...STARTING...
Timestamp     A/R    Flags if Hostname                               Address                                      TTL
15:06:24.220  Add 40000003  6 faec1782-f0b7-40a3-bbe2-5f7dae0164eb.local. FE80:0000:0000:0000:24F8:E8C4:904C:074E%awdl0 4500
15:06:24.220  Add 40000003  6 faec1782-f0b7-40a3-bbe2-5f7dae0164eb.local. FE80:0000:0000:0000:6DBC:0C9A:93DB:6A03%awdl0 4500
15:06:24.220  Add 40000003  6 faec1782-f0b7-40a3-bbe2-5f7dae0164eb.local. FE80:0000:0000:0000:10A0:A1A1:BE5A:5557%awdl0 4500
15:06:24.220  Add 40000003  6 faec1782-f0b7-40a3-bbe2-5f7dae0164eb.local. FE80:0000:0000:0000:2295:0C6B:FB09:165E%awdl0 4500
15:06:24.220  Add 40000003  6 faec1782-f0b7-40a3-bbe2-5f7dae0164eb.local. FE80:0000:0000:0000:3042:43FF:FE5C:8E72%awdl0 4500
15:06:24.220  Add 40000003  6 faec1782-f0b7-40a3-bbe2-5f7dae0164eb.local. FE80:0000:0000:0000:0428:54A1:39AF:8521%awdl0 4500
15:06:24.220  Add 40000002  6 faec1782-f0b7-40a3-bbe2-5f7dae0164eb.local. FE80:0000:0000:0000:0000:0000:0000:0001%awdl0 4500
^C
```

You can find all the code for the mDNS implementation [here](https://github.com/deanward81/AirDropAnywhere/tree/main/src/AirDropAnywhere.Core/MulticastDns).

## Next time...
This post is already getting pretty lengthy so I'll wrap it up for now. Next time we'll go into implementing the HTTP API for AirDrop. This should be relatively simple - the [OpenDrop](https://github.com/seemoo-lab/opendrop) and [PrivateDrop](https://github.com/seemoo-lab/privatedrop) projects have implementations in Python and Swift that we can use as a basis for a Kestrel-based implementation.

