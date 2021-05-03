---
title: "AirDrop Anywhere - Part 1 - Introduction"
date: 2021-05-03T15:00:00Z
tags: [.net, networking, airdrop, apple]
images: [img/airdrop-anywhere-cover.jpg]
---

> This is part 1 of a series of posts:
> - [Part 1 - Introduction](/posts/2021-05-airdrop-anywhere-part-1)
> - [Part 2 - Writing some code](/posts/2021-05-airdrop-anywhere-part-2)

Lately I've been frustrated at the pain of transferring files from an Apple device to Windows - when transferring between Apple devices it's dead simple using AirDrop. I know there are many applications out there that allow files to be transferred as simply between any platform, but they require installation on each device and it seems like unnecessary hassle. Ideally AirDrop would work natively with Windows, but I can't see Apple enabling that anytime soon!

Luckily I know how to write da codez and this feels like a project with enough grit to it that it feels like it'd be kind of fun! So I've decided to embark upon a side-project to attempt to implement AirDrop on Windows - this might amount to nothing, but, hey, it could be an interesting learning experience, so let's give it a shot!

This is part one of an, as yet, undetermined number of posts documenting the journey to implementing AirDrop - or something approximating it!

## Breaking down the problem

After a few hours of digging around the problem and working out what needed to be done I embarked on a path that, eventually, turned out to be a dead end. I'll write about it here just to demonstrate the false starts that often accompany any project :).

### A first stab...

AirDrop uses a protocol called Apple Wireless Direct Link (AWDL) which is used to establish a peer-to-peer wireless connection between the sender and receiver _without them being connected to the same access point_. A whole lot of people at the [Secure Mobile Networking Lab](https://github.com/seemoo-lab) have been working to reverse engineer it and that work has resulted in the [OWL project](https://owlink.org) which has a bunch of publications breaking down the protocol and its security implications as well as open source code in C, Python and Swift that provide a reference implementation of that protocol (called OWL - Open Wireless Link) and AirDrop itself.

However OWL is written to work on macOS and Linux - it makes use of `libpcap` and various libraries that only work on those platforms.

Under the hood OWL uses `libpcap` to create a virtual network adapter and uses raw frame manipulation from userspace code to talk to the underlying network stack. The project has a [great diagram](https://github.com/seemoo-lab/owl#architecture) detailing their interactions with the different parts of the system:

<img src="https://raw.githubusercontent.com/seemoo-lab/owl/master/resources/overview.png" width=480 alt="OWL Architecture"><br/>
<sub style="color:lightgray">OWL Architecture</sub>

To get this whole shebang working under Windows we need a similar approach of capturing and manipulating raw network packets. I recalled that [Wireshark](https://www.wireshark.org/) used to install a packet capture driver that implemented the NDIS interfaces provided by Windows. Turns out the original way it did this was with [Winpcap](https://www.winpcap.org/) but there's a note saying that it has been deprecated in favour of [npcap](https://nmap.org/npcap/) which is part of the nmap project. Sadly the licensing for npcap is just a tad expensive for any kind of distribution - like an app that could live in the Windows Store - if this project ever gets to that point then I'd probably consider writing a TAP driver much like what OpenVPN uses (see their [TAP driver repo](https://github.com/OpenVPN/tap-windows6)).

I should note that, prior to reading the OWL publications, I was under the impression that this might be implemented using something like [Wintun](https://www.wintun.net). This is similar in many ways to npcap but it works uses a TUN driver rather than a TAP one. TUN drivers operate at layer 3 of the OSI stack whereas TAP drivers operate at layer 2 - Wikipedia [has a nice diagram](https://en.wikipedia.org/wiki/TUN/TAP) illustrating the differences. To implement AWDL we need to be able to manipulate radiotap and 802.11 on the WLAN interface that we are connected to - these are transport layer (i.e. L2) concerns so we need to use something that operates at that layer.

npcap is shipped as an installer that needs to be run in order to configure the underlying network driver. For our purposes we need to install it with the "Support raw 802.11 traffic (and monitor mode) for wireless adapters" option switched on:

<img src="/img/airdrop-anywhere-1.png" width=480 alt="Installing npcap"><br/>
<sub style="color:lightgray">Installing npcap</sub>

I was about to sit down and write the [P/Invoke](https://docs.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke) code necessary to get .NET talking to this native library. That usually involves repetitive dissection of the header files provided with the library to construct the appropriate type signatures but, thankfully, the hard work has already been done by the maintainers of [SharpPcap](https://github.com/chmorgan/sharppcap). This library provides us with a way of initiating a packet capture and manipulating packets as we see fit. Yay!

Additionally, there's [Packet.NET](https://github.com/chmorgan/packetnet) which provides a way to parse the raw packets that are being captured. This should be very helpful in allowing us to put together the protocol implementation needed for AWDL.

But... this is where I hit a stumbling block - both my Windows desktop and laptop machines have wireless adapters but neither support [monitor mode](https://en.wikipedia.org/wiki/Monitor_mode) (aka RFMON) and whilst I could go and buy one easily enough, it appears that supporting monitor mode is not commonplace on Windows. Apple have gone the extra mile in shipping wireless hardware allows the AirDrop experience to be as seamless as possible but, sadly, that makes writing something that could be used by anybody on a Windows platform practically impossible. Eurgh.

### Taking a couple of steps back

At this point I started to consider the project a dead-end and just as I was switching off the laptop I thought - surely AirDrop is supported on wired adapters too? Turns out [it is](https://www.lifewire.com/airdrop-with-without-wifi-connection-2259801) but with some caveats - this is a legacy feature and AirDrop on any Mac that executes the following command is only available on networks that the Mac is connected to - no adhoc peer-to-peer fun!

```
defaults write com.apple.NetworkBrowser BrowseAllInterfaces 1
```

But, for our case of operating on Windows, that might be the glimmer of hope I'm looking for! The [OWL project](https://owlink.org) actually has an implementation of the services needed by AirDrop using the [Bonjour protocol](https://en.wikipedia.org/wiki/Bonjour_(software)).  Apple [provides support](https://developer.apple.com/bonjour/) for running Bonjour on Windows but it'd be nice to have a service that could run on any platform supported by .NET. While there are a whole bunch of libraries out there that support _browsing_ Bonjour-based services there's only one that seems to be maintained for _advertising_ services using it. Under the hood Bonjour is really an implementation of DNS-SD which uses multicast DNS as its basis. [Richard Schneider](https://github.com/richardschneider/net-mdns) has implemented an [mDNS](https://en.wikipedia.org/wiki/Multicast_DNS) library that supports advertising services - this might be the magic we're looking for!

### More bad news :/

After switching on the `BrowseAllInterfaces` option in macOS and throwing together some code that advertises a service over mDNS I quickly discovered (by using [Wireshark](https://www.wireshark.org/) to monitor the network adapters on my MacBook) that iPhones and iPads will only deal with AirDrop if it's advertised over the AWDL interface (`awdl0` in macOS). This effectively makes this approach useless for using AirDrop between an iPhone and any machine that does not run AWDL (read: Apple devices) or OWL (read: Linux). Another dead end!

### What else?!
At this point I thought of another approach - if we were to implement our AirDrop implementation as a kind of "proxy" we could allow arbitrary machines to register themselves with the proxy and devices supporting AirDrop natively would be able to discover them and send/receive files to them. _In theory_ this should work - we'll need a machine that supports AWDL to act as the proxy (either an Apple device or Linux running the OWL implementation) and then we'll advertise AirDrop using mDNS, exposing a Kestrel-based set of endpoints that implement AirDrop. It's not ideal - it requires a machine to be running the proxy all the time, but in the interests of science, well, this might just work!

Next time we'll dig into the beginnings of an AirDrop proxy on macOS / Linux that can advertise subscribed Windows machines, allowing them to both send and receive using AirDrop. Stay tuned!
